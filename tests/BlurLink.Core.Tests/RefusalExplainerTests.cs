using BlurLink.Shell.ViewModels;
using Xunit;

namespace BlurLink.Core.Tests;

public sealed class RefusalExplainerTests
{
    [Theory]
    [InlineData("dedup")]
    [InlineData("rate")]
    [InlineData("payload-gate")]
    [InlineData("fragments")]
    [InlineData("host-broadcast")]
    [InlineData("host-ambiguous")]
    [InlineData("host-unmatched")]
    [InlineData("host-collision")]
    [InlineData("announce-rejected")]
    [InlineData("injection-error")]
    public void EveryKnownCode_ExplainsNonEmpty(string code)
        => Assert.NotEmpty(RefusalExplainer.Explain(code));

    [Fact]
    public void UnknownCode_SaysSo()
        => Assert.Equal("unknown refusal", RefusalExplainer.Explain("nope"));
}
