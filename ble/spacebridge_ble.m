// CoreBluetooth behind a small C interface, for the .NET side (Ble.cs).
// Every CoreBluetooth call runs on one serial queue; the exported functions block the caller until the step is done.
#import <CoreBluetooth/CoreBluetooth.h>

typedef void (*sb_advert_cb)(const char *peripheral, const uint8_t *manufacturer, int length);
typedef void (*sb_data_cb)(void *context, const uint8_t *data, int length); // length < 0: the link is gone

static NSString *const SaltoService = @"B6E60001-E2E3-BC82-4C72-929D0D29CA17";

@interface SBLink : NSObject
@property CBPeripheral *peripheral;
@property CBCharacteristic *readChar, *writeChar;
@property sb_data_cb onData;
@property void *context;
@property dispatch_semaphore_t step; // signalled when the pending step is over
@property NSError *error;
@property NSData *value;
@property BOOL reading, connected;
@end
@implementation SBLink
@end

@interface SBCentral : NSObject <CBCentralManagerDelegate, CBPeripheralDelegate>
@end

static dispatch_queue_t queue;
static CBCentralManager *central;
static SBCentral *handler;
static NSMutableDictionary<NSString *, CBPeripheral *> *seen;
static NSMutableDictionary<NSString *, SBLink *> *links;
static sb_advert_cb advert;

static SBLink *linkOf(CBPeripheral *peripheral) { return links[peripheral.identifier.UUIDString]; }

static void done(SBLink *link, NSError *error) {
    if (!link) return;
    link.error = error;
    if (link.step) dispatch_semaphore_signal(link.step);
}

@implementation SBCentral
- (void)centralManagerDidUpdateState:(CBCentralManager *)manager {
}

- (void)centralManager:(CBCentralManager *)manager didDiscoverPeripheral:(CBPeripheral *)peripheral
     advertisementData:(NSDictionary<NSString *, id> *)data RSSI:(NSNumber *)rssi {
    NSData *manufacturer = data[CBAdvertisementDataManufacturerDataKey];
    const uint8_t *bytes = manufacturer.bytes;
    // Company id 409 (SALTO), little endian: only those are kept for connecting.
    if (manufacturer.length >= 2 && bytes[0] == 0x99 && bytes[1] == 0x01) seen[peripheral.identifier.UUIDString] = peripheral;
    if (advert) advert(peripheral.identifier.UUIDString.UTF8String, bytes, (int)manufacturer.length);
}

- (void)centralManager:(CBCentralManager *)manager didConnectPeripheral:(CBPeripheral *)peripheral {
    linkOf(peripheral).connected = YES;
    done(linkOf(peripheral), nil);
}

- (void)centralManager:(CBCentralManager *)manager didFailToConnectPeripheral:(CBPeripheral *)peripheral error:(NSError *)error {
    done(linkOf(peripheral), error ?: [NSError errorWithDomain:@"spacebridge" code:1 userInfo:@{NSLocalizedDescriptionKey: @"connection failed"}]);
}

- (void)centralManager:(CBCentralManager *)manager didDisconnectPeripheral:(CBPeripheral *)peripheral error:(NSError *)error {
    SBLink *link = linkOf(peripheral);
    if (!link) return;
    link.connected = NO;
    done(link, error ?: [NSError errorWithDomain:@"spacebridge" code:2 userInfo:@{NSLocalizedDescriptionKey: @"disconnected"}]);
    if (link.onData) link.onData(link.context, NULL, -1);
}

- (void)peripheral:(CBPeripheral *)peripheral didDiscoverServices:(NSError *)error {
    done(linkOf(peripheral), error);
}

- (void)peripheral:(CBPeripheral *)peripheral didDiscoverCharacteristicsForService:(CBService *)service error:(NSError *)error {
    done(linkOf(peripheral), error);
}

- (void)peripheral:(CBPeripheral *)peripheral didWriteValueForCharacteristic:(CBCharacteristic *)characteristic error:(NSError *)error {
    done(linkOf(peripheral), error);
}

- (void)peripheral:(CBPeripheral *)peripheral didUpdateNotificationStateForCharacteristic:(CBCharacteristic *)characteristic error:(NSError *)error {
    done(linkOf(peripheral), error);
}

- (void)peripheral:(CBPeripheral *)peripheral didUpdateValueForCharacteristic:(CBCharacteristic *)characteristic error:(NSError *)error {
    SBLink *link = linkOf(peripheral);
    if (!link) return;
    NSData *value = [characteristic.value copy];
    if (link.reading) {
        // The answer to an explicit read.
        link.reading = NO;
        link.value = value;
        done(link, error);
    } else if (!error && link.onData) {
        link.onData(link.context, value.bytes, (int)value.length);
    }
}
@end

static void ensure(void) {
    static dispatch_once_t once;
    dispatch_once(&once, ^{
        queue = dispatch_queue_create("cz.totosoft.spacebridge.ble", DISPATCH_QUEUE_SERIAL);
        seen = [NSMutableDictionary new];
        links = [NSMutableDictionary new];
        handler = [SBCentral new];
        central = [[CBCentralManager alloc] initWithDelegate:handler queue:queue options:@{CBCentralManagerOptionShowPowerAlertKey: @NO}];
    });
}

// Runs one step on the queue and waits for the delegate to report it done.
static BOOL step(SBLink *link, int timeoutMs, void (^start)(void)) {
    link.error = nil;
    link.step = dispatch_semaphore_create(0);
    dispatch_async(queue, start);
    BOOL finished = dispatch_semaphore_wait(link.step, dispatch_time(DISPATCH_TIME_NOW, (int64_t)timeoutMs * NSEC_PER_MSEC)) == 0;
    if (!finished) link.error = [NSError errorWithDomain:@"spacebridge" code:3 userInfo:@{NSLocalizedDescriptionKey: @"timeout"}];
    return finished && link.error == nil;
}

static void *fail(char *error, int size, NSString *message, SBLink *link) {
    if (error && size > 0) snprintf(error, size, "%s", message.UTF8String);
    if (link) dispatch_sync(queue, ^{
        [links removeObjectForKey:link.peripheral.identifier.UUIDString];
        [central cancelPeripheralConnection:link.peripheral];
    });
    return NULL;
}

// CBManagerState: 0 unknown, 1 resetting, 2 unsupported, 3 unauthorized, 4 powered off, 5 powered on.
int sb_ble_state(int waitMs) {
    ensure();
    NSDate *until = [NSDate dateWithTimeIntervalSinceNow:waitMs / 1000.0];
    while (central.state == CBManagerStateUnknown && until.timeIntervalSinceNow > 0) usleep(50 * 1000);
    return (int)central.state;
}

// CBManagerAuthorization: 0 not asked or not answered yet, 1 restricted, 2 denied, 3 allowed.
int sb_ble_authorization(void) {
    return (int)CBCentralManager.authorization;
}

int sb_ble_scan(sb_advert_cb callback) {
    if (sb_ble_state(0) != CBManagerStatePoweredOn) return -1;
    dispatch_sync(queue, ^{
        advert = callback;
        [central scanForPeripheralsWithServices:nil options:@{CBCentralManagerScanOptionAllowDuplicatesKey: @YES}];
    });
    return 0;
}

void sb_ble_scan_stop(void) {
    ensure();
    dispatch_sync(queue, ^{
        [central stopScan];
        advert = NULL;
    });
}

// Connects to a peripheral heard in a scan and finds SALTO's service and its two characteristics.
void *sb_ble_open(const char *peripheralId, sb_data_cb onData, void *context, int timeoutMs, char *error, int errorSize) {
    if (sb_ble_state(2000) != CBManagerStatePoweredOn) return fail(error, errorSize, @"Bluetooth is not on or not allowed", nil);
    NSString *identifier = [NSString stringWithUTF8String:peripheralId];
    __block CBPeripheral *peripheral;
    dispatch_sync(queue, ^{
        peripheral = seen[identifier];
    });
    if (!peripheral) return fail(error, errorSize, @"BluetoothLE Device creation failed", nil);

    SBLink *link = [SBLink new];
    link.peripheral = peripheral;
    link.onData = onData;
    link.context = context;
    dispatch_sync(queue, ^{
        links[identifier] = link;
        peripheral.delegate = handler;
    });

    if (!step(link, timeoutMs, ^{ [central connectPeripheral:peripheral options:nil]; }))
        return fail(error, errorSize, [@"Connection failed: " stringByAppendingString:link.error.localizedDescription], link);
    CBUUID *uuid = [CBUUID UUIDWithString:SaltoService];
    if (!step(link, timeoutMs, ^{ [peripheral discoverServices:@[uuid]]; }) || peripheral.services.count == 0)
        return fail(error, errorSize, @"Gatt Salto Service discovery failed", link);
    CBService *service = peripheral.services[0];
    if (!step(link, timeoutMs, ^{ [peripheral discoverCharacteristics:nil forService:service]; }))
        return fail(error, errorSize, @"Gatt Salto Service Characteristics discovery failed", link);
    NSArray<CBCharacteristic *> *found = service.characteristics;
    if (found.count != 2)
        return fail(error, errorSize, [NSString stringWithFormat:@"Gatt Salto Service does not have the required characteristics (%lu)", (unsigned long)found.count], link);
    // As the Windows bridge: the first is read from, the second written to. If the system lists them the other way round, by what they can do.
    CBCharacteristic *readChar = found[0], *writeChar = found[1];
    if (!(writeChar.properties & CBCharacteristicPropertyWrite) && (readChar.properties & CBCharacteristicPropertyWrite)) {
        readChar = found[1];
        writeChar = found[0];
    }
    if (!(writeChar.properties & CBCharacteristicPropertyWrite)) return fail(error, errorSize, @"Gatt Salto Service Write Characteristic has wrong properties", link);
    if (!(readChar.properties & CBCharacteristicPropertyRead)) return fail(error, errorSize, @"Gatt Salto Service Read Characteristic has wrong properties", link);
    link.readChar = readChar;
    link.writeChar = writeChar;
    return (__bridge_retained void *)link;
}

// What one write without response may carry: the ATT MTU less its 3-byte header.
int sb_ble_mtu(void *handle) {
    SBLink *link = (__bridge SBLink *)handle;
    return (int)[link.peripheral maximumWriteValueLengthForType:CBCharacteristicWriteWithoutResponse];
}

int sb_ble_write(void *handle, const uint8_t *data, int length, int withResponse, int timeoutMs) {
    SBLink *link = (__bridge SBLink *)handle;
    if (!link.connected) return -1;
    NSData *value = [NSData dataWithBytes:data length:length];
    if (withResponse)
        return step(link, timeoutMs, ^{ [link.peripheral writeValue:value forCharacteristic:link.writeChar type:CBCharacteristicWriteWithResponse]; }) ? 0 : -1;
    // Without response there is no confirmation; wait only while the system's send buffer is full.
    for (int waited = 0; !link.peripheral.canSendWriteWithoutResponse; waited += 5) {
        if (waited >= timeoutMs || !link.connected) return -1;
        usleep(5 * 1000);
    }
    dispatch_sync(queue, ^{ [link.peripheral writeValue:value forCharacteristic:link.writeChar type:CBCharacteristicWriteWithoutResponse]; });
    return 0;
}

int sb_ble_read(void *handle, uint8_t *buffer, int capacity, int timeoutMs) {
    SBLink *link = (__bridge SBLink *)handle;
    if (!link.connected) return -1;
    link.value = nil;
    link.reading = YES;
    BOOL ok = step(link, timeoutMs, ^{ [link.peripheral readValueForCharacteristic:link.readChar]; });
    link.reading = NO;
    if (!ok || !link.value) return -1;
    int length = (int)MIN(link.value.length, (NSUInteger)capacity);
    memcpy(buffer, link.value.bytes, length);
    return length;
}

int sb_ble_notify(void *handle, int timeoutMs) {
    SBLink *link = (__bridge SBLink *)handle;
    if (!link.connected) return -1;
    return step(link, timeoutMs, ^{ [link.peripheral setNotifyValue:YES forCharacteristic:link.readChar]; }) ? 0 : -1;
}

void sb_ble_close(void *handle) {
    SBLink *link = (__bridge_transfer SBLink *)handle;
    dispatch_sync(queue, ^{
        link.onData = NULL;
        [links removeObjectForKey:link.peripheral.identifier.UUIDString];
        [central cancelPeripheralConnection:link.peripheral];
    });
}
