# brew tap berrysekk/spacebridge https://github.com/Berrysekk/spacebridge
# brew install spacebridge
class Spacebridge < Formula
  desc "SALTO ProAccess SPACE Local IO Bridge for macOS"
  homepage "https://github.com/Berrysekk/spacebridge"
  url "https://github.com/Berrysekk/spacebridge/releases/download/v2.0.4/spacebridge-2.0.4-macos-arm64.tar.gz"
  version "2.0.4"
  sha256 "cd57392b006b42d4b4329bb7567afd73f9b53dd440fb2a57093ba39bc8115fc4"

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
