# brew tap berrysekk/spacebridge https://github.com/Berrysekk/SaltoSpacebridge-macos
# brew install spacebridge
class Spacebridge < Formula
  desc "SALTO ProAccess SPACE Local IO Bridge for macOS"
  homepage "https://github.com/Berrysekk/SaltoSpacebridge-macos"
  url "https://github.com/Berrysekk/SaltoSpacebridge-macos/releases/download/v1.0.0/spacebridge-1.0.0-macos-arm64.tar.gz"
  version "1.0.0"
  sha256 "b37e02e3d99908b5e3e6c98bb72d69b46dbf8a93c93b609afd69e548da8b9fa4"

  depends_on arch: :arm64
  depends_on :macos

  def install
    # Homebrew unpacks into the archive's only folder, Spacebridge.app.
    (libexec/"Spacebridge.app").install "Contents"
    bin.install_symlink libexec/"Spacebridge.app/Contents/MacOS/spacebridge"
  end

  def caveats
    <<~EOS
      Set the bridge up for your SPACE (fetches SALTO's signing key from it, installs the background service):
        spacebridge setup https://your-space:8100
      After an upgrade, update the service:
        spacebridge install
    EOS
  end

  test do
    assert_match version.to_s, shell_output("#{bin}/spacebridge version")
  end
end
