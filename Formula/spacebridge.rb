# brew tap berrysekk/spacebridge https://github.com/Berrysekk/spacebridge
# brew install spacebridge
class Spacebridge < Formula
  desc "SALTO ProAccess SPACE Local IO Bridge for macOS"
  homepage "https://github.com/Berrysekk/spacebridge"
  url "https://github.com/Berrysekk/spacebridge/releases/download/v2.0.3/spacebridge-2.0.3-macos-arm64.tar.gz"
  version "2.0.3"
  sha256 "bc3f8b9b147470e4f41c8fb6b6ef9b3cbdf394c0a1bc9ff534f82d3d17daa6f2"

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
