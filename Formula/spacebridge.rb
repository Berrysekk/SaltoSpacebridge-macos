# brew tap berrysekk/spacebridge https://github.com/Berrysekk/SaltoSpacebridge-macos
# brew install spacebridge
class Spacebridge < Formula
  desc "SALTO ProAccess SPACE Local IO Bridge for macOS"
  homepage "https://github.com/Berrysekk/SaltoSpacebridge-macos"
  url "https://github.com/Berrysekk/SaltoSpacebridge-macos/releases/download/v1.0.1/spacebridge-1.0.1-macos-arm64.tar.gz"
  version "1.0.1"
  sha256 "0558d7e7d866c5e745f2979fe9485503302d4ed4edc3a03d258db49444c3e6cc"

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
