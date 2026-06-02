using DiffPdf.Api.Auth;

namespace DiffPdf.Api.Tests;

public class PasswordHasherTests
{
    [Fact]
    public void Hash_Then_Verify_Succeeds()
    {
        string hash = PasswordHasher.Hash("s3cret-pass");
        Assert.StartsWith("pbkdf2$", hash);
        Assert.True(PasswordHasher.VerifyHash("s3cret-pass", hash));
        Assert.False(PasswordHasher.VerifyHash("wrong", hash));
    }

    [Fact]
    public void VerifyHash_RejectsMalformed()
    {
        Assert.False(PasswordHasher.VerifyHash("x", "not-a-hash"));
        Assert.False(PasswordHasher.VerifyHash("x", "pbkdf2$abc$def"));
    }

    [Theory]
    [InlineData("hunter2", "hunter2", true)]
    [InlineData("hunter2", "Hunter2", false)]
    [InlineData("", "", true)]
    public void VerifyPlaintext_ConstantTimeCompare(string candidate, string expected, bool ok) =>
        Assert.Equal(ok, PasswordHasher.VerifyPlaintext(candidate, expected));

    [Fact]
    public void AuthUser_PrefersHashOverPlaintext()
    {
        var user = new AuthUser { Username = "u", PasswordHash = PasswordHasher.Hash("real"), Password = "ignored" };
        Assert.True(user.VerifyPassword("real"));
        Assert.False(user.VerifyPassword("ignored"));
    }
}
