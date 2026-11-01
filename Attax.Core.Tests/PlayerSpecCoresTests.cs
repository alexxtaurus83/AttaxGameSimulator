using System;
using Attax.Play;
using Xunit;

namespace Attax.Core.Tests {
    public class PlayerSpecCoresTests {
        [Fact]
        public void Cores_ParsesCorrectly_OnSearchAndPolicy() {
            var c = PlayerSpec.Parse("classic:depth=3,cores=4");
            Assert.Equal(4, c.Cores);
            Assert.Contains("cores=4", c.ToString());

            var v = PlayerSpec.Parse("value:model=test.onnx,depth=2,cores=2");
            Assert.Equal(2, v.Cores);
            Assert.Contains("cores=2", v.ToString());

            var p = PlayerSpec.Parse("policy:model=test.onnx,cores=8");
            Assert.Equal(8, p.Cores);
            Assert.Contains("cores=8", p.ToString());
        }

        [Theory]
        [InlineData("classic:depth=3,cores=0")]
        [InlineData("classic:depth=3,cores=65")]
        [InlineData("classic:depth=3,cores=-1")]
        [InlineData("classic:depth=3,cores=abc")]
        [InlineData("random:cores=2")]
        public void Cores_RejectsInvalidValues_AndRandomKind(string specText) {
            Assert.Throws<FormatException>(() => PlayerSpec.Parse(specText));
        }

        [Fact]
        public void Cores_RoundTrip_AndOmittedBehaviorPreserved() {
            var specNoCores = PlayerSpec.Parse("classic:depth=3");
            Assert.Null(specNoCores.Cores);
            Assert.DoesNotContain("cores", specNoCores.ToString());

            var specWithCores = PlayerSpec.Parse("classic:depth=3,cores=4");
            var roundTripped = PlayerSpec.Parse(specWithCores.ToString());
            Assert.Equal(specWithCores, roundTripped);
            Assert.Equal(4, roundTripped.Cores);
        }
    }
}
