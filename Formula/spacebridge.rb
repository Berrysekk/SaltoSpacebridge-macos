# brew tap berrysekk/spacebridge https://github.com/Berrysekk/SaltoSpacebridge-macos
# brew install spacebridge
class Spacebridge < Formula
  desc "SALTO ProAccess SPACE Local IO Bridge for macOS"
  homepage "https://github.com/Berrysekk/SaltoSpacebridge-macos"
  url "https://github.com/Berrysekk/SaltoSpacebridge-macos/releases/download/v2.0.1/spacebridge-2.0.1-macos-arm64.tar.gz"
  version "2.0.1"
  sha256 "0a7e58758e0798d995864b13139b72f56123d3356be6bf0234f80e5975738914"

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
