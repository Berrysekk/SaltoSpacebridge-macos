# brew tap berrysekk/spacebridge https://github.com/Berrysekk/SaltoSpacebridge-macos
# brew install spacebridge
class Spacebridge < Formula
  desc "SALTO ProAccess SPACE Local IO Bridge for macOS"
  homepage "https://github.com/Berrysekk/SaltoSpacebridge-macos"
  url "https://github.com/Berrysekk/SaltoSpacebridge-macos/releases/download/v1.1.0/spacebridge-1.1.0-macos-arm64.tar.gz"
  version "1.1.0"
  sha256 "ccb5fb8f57c4fe4ca4da1a2d4ac3f0d2a35668bb447ca6247de47f4e75bddac7"

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
