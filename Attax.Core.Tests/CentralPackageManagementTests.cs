using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace Attax.Core.Tests {
    public class CentralPackageManagementTests {
        private static string FindRepoRoot() {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null) {
                if (File.Exists(Path.Combine(dir.FullName, "Attax.sln")) &&
                    File.Exists(Path.Combine(dir.FullName, "Directory.Packages.props"))) {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
            throw new InvalidOperationException("Could not find repo root containing Attax.sln and Directory.Packages.props");
        }

        [Fact]
        public void DirectoryPackagesProps_EnforcesCentralPackageManagement() {
            string repoRoot = FindRepoRoot();
            string propsPath = Path.Combine(repoRoot, "Directory.Packages.props");
            Assert.True(File.Exists(propsPath), $"Directory.Packages.props must exist at repo root: {propsPath}");

            var doc = XDocument.Load(propsPath);
            var root = doc.Root;
            Assert.NotNull(root);

            var manageElem = root.Descendants("ManagePackageVersionsCentrally").FirstOrDefault();
            Assert.NotNull(manageElem);
            Assert.Equal("true", manageElem.Value.Trim(), ignoreCase: true);

            var overrideElem = root.Descendants("CentralPackageVersionOverrideEnabled").FirstOrDefault();
            Assert.NotNull(overrideElem);
            Assert.Equal("false", overrideElem.Value.Trim(), ignoreCase: true);
        }

        [Fact]
        public void AllCsprojFiles_DoNotContainInlinePackageVersions() {
            string repoRoot = FindRepoRoot();
            var csprojFiles = Directory.GetFiles(repoRoot, "*.csproj", SearchOption.AllDirectories)
                .Where(p => !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) &&
                            !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                .ToList();

            Assert.NotEmpty(csprojFiles);

            foreach (var csproj in csprojFiles) {
                var doc = XDocument.Load(csproj);
                var packageRefs = doc.Descendants("PackageReference").ToList();

                foreach (var pref in packageRefs) {
                    var pkgName = pref.Attribute("Include")?.Value ?? pref.Attribute("Update")?.Value ?? "(unknown)";
                    var versionAttr = pref.Attribute("Version");
                    var overrideAttr = pref.Attribute("VersionOverride");

                    Assert.True(versionAttr == null,
                        $"Project {Path.GetFileName(csproj)} specifies inline Version for package '{pkgName}'. Versions must be centrally managed in Directory.Packages.props.");
                    Assert.True(overrideAttr == null,
                        $"Project {Path.GetFileName(csproj)} specifies VersionOverride for package '{pkgName}'. Version overrides are disallowed.");
                }
            }
        }
    }
}
