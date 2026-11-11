# brew tap totosoftsro/spacebridge https://github.com/totosoftsro/SaltoSpacebridge-macos
# brew install spacebridge
class Spacebridge < Formula
  desc "SALTO ProAccess SPACE Local IO Bridge for macOS"
  homepage "https://github.com/totosoftsro/SaltoSpacebridge-macos"
  url "https://github.com/totosoftsro/SaltoSpacebridge-macos/releases/download/v1.0.0/spacebridge-1.0.0-macos-arm64.tar.gz"
  version "1.0.0"
  sha256 "4d6de57911cb1fa4b69fc8c98c0253366145b8ddbe31b8c186970b55423ac849"

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
