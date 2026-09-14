using GitHistory.Core.Services;

namespace GitHistory.Tests.Desktop;

public sealed class LocalCheckoutPathsTests
{
    private static string Root => Path.Combine(Path.GetTempPath(), "GitHistory path tests", "checkout");

    [Fact]
    public void Git_paths_resolve_under_checkout_without_changing_case_spaces_or_shell_characters()
    {
        string target = LocalCheckoutPaths.Resolve(Root, "src/MiXeD/A & B$(value); file.cs");

        Assert.Equal(Path.Combine(Root, "src", "MiXeD", "A & B$(value); file.cs"), target);
        Assert.True(LocalCheckoutPaths.IsWithinRoot(Root, target));
        Assert.Equal(Root, LocalCheckoutPaths.Resolve(Root + Path.DirectorySeparatorChar, null));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("src/../../outside.txt")]
    [InlineData("src/./file.cs")]
    [InlineData("/absolute.txt")]
    [InlineData("C:/outside.txt")]
    [InlineData("C:outside.txt")]
    [InlineData("\\\\server\\share\\file.txt")]
    [InlineData("src\\..\\outside.txt")]
    [InlineData("src//file.cs")]
    [InlineData("src/trailing. ")]
    [InlineData("src/file.cs:stream")]
    [InlineData("src/CON.txt")]
    [InlineData("src/LPT1")]
    [InlineData("src/NUL")]
    [InlineData(".git/config")]
    [InlineData("src/has\nnewline.cs")]
    public void Paths_that_escape_or_cannot_be_represented_on_Windows_are_rejected(string path)
    {
        Assert.Throws<ArgumentException>(() => LocalCheckoutPaths.Resolve(Root, path));
    }

    [Fact]
    public void A_link_target_in_a_sibling_with_the_same_prefix_is_outside_the_checkout()
    {
        string sibling = Root + "-other" + Path.DirectorySeparatorChar + "private.txt";

        Assert.False(LocalCheckoutPaths.IsWithinRoot(Root, sibling));
        Assert.Throws<ArgumentException>(() => LocalCheckoutPaths.EnsureWithinRoot(Root, sibling));
        LocalCheckoutPaths.EnsureWithinRoot(Root, Path.Combine(Root, "src", "safe.cs"));
    }

    [Fact]
    public void Relative_checkout_roots_are_not_resolved_against_the_application_directory()
    {
        Assert.Throws<ArgumentException>(() => LocalCheckoutPaths.Resolve("relative checkout", "src/file.cs"));
    }
}
