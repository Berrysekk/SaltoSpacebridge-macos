# brew tap berrysekk/spacebridge https://github.com/Berrysekk/SaltoSpacebridge-macos
# brew install spacebridge
class Spacebridge < Formula
  desc "SALTO ProAccess SPACE Local IO Bridge for macOS"
  homepage "https://github.com/Berrysekk/SaltoSpacebridge-macos"
  url "https://github.com/Berrysekk/SaltoSpacebridge-macos/releases/download/v2.0.0/spacebridge-2.0.0-macos-arm64.tar.gz"
  version "2.0.0"
  sha256 "a0f1a9db7832c35c0b743b46d55ca1e799439ccc491e76254e3c012f1531460c"

  depends_on arch: :arm64
  depends_on :macos

  def install
    # Homebrew unpacks into the archive's only folder, Spacebridge.app.
    (libexec/"Spacebridge.app").install "Contents"
    bin.install_symlink libexec/"Spacebridge.app/Contents/MacOS/spacebridge"
  end

  def caveats
    <<~EOS
      Set the bridge up for your SPACE (a few questions, then the background service):
        spacebridge setup
      After an upgrade, update the service:
        spacebridge install
    EOS
  end

  test do
    assert_match version.to_s, shell_output("#{bin}/spacebridge version")
  end
end
