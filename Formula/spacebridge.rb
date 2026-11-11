# brew tap berrysekk/spacebridge https://github.com/Berrysekk/spacebridge
# brew install spacebridge
class Spacebridge < Formula
  desc "SALTO ProAccess SPACE Local IO Bridge for macOS"
  homepage "https://github.com/Berrysekk/spacebridge"
  url "https://github.com/Berrysekk/spacebridge/releases/download/v2.0.6/spacebridge-2.0.6-macos-arm64.tar.gz"
  version "2.0.6"
  sha256 "b09ea578b0b3c0fed1419aab9f36a312c4476167abe609f43ecb2bbf0f1a9e9c"

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
