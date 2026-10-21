using BikeSync.Core.Providers;

namespace BikeSync.Tests;

public sealed class PlatformCompatibilityTests
{
    // Public key material returned by iGPSPORT's own public key endpoint. It is a
    // client-side obfuscation key, not a user credential.
    private const string PublicKey =
        "WlZASuWX2yCe2V9qXj32qFl7PfKhJxhXBoUaiJydV3qkC6HYEOmmL9XbrpWa8emRvcskyoWKlBy5XYomeUASBKXQPpEFgxV3PE2c5xAssK0vTy2md2IOMhX6QumKeH/MeMqtqsmAxBQeBCLG4DVbQBNfFbdcpIs9sl+AFrcASwEQHxkJcRCtj3aUx/a0T+CLh5AW/osL5HQo5ni6jJLBF9/KFt/ZiXmTRQhDRRQxoDdiWw2YAvKP/6vL2Ydz+L2kdj1laZL/1IDhSR7d05n61bEGn4fDZh9LtzJg+lbAx//gXObKkug34/5fNEuxNSyQwcY2jal+Fk8Gu3sx3q28rg==";

    [Fact]
    public void IGpsport_signer_matches_public_web_client()
    {
        using var signer = new IGpsportSigner();
        signer.Initialize(PublicKey);

        var signature = signer.Sign(
            "GET",
            "/service/edge-core/api/public/key",
            "1792591367",
            "abc123",
            string.Empty);

        Assert.Equal("RH9A3iJftNA2Clx0btrV2p3jtavr5rsJrK+zyKd5/rg=", signature);
    }

    [Fact]
    public void MyWhoosh_encrypted_envelope_has_expected_shape()
    {
        var encoded = MyWhooshApi.EncryptFileRequest("test");
        var bytes = Convert.FromBase64String(encoded);

        Assert.True(bytes.Length > 32);
        Assert.Equal("Salted__", System.Text.Encoding.ASCII.GetString(bytes, 0, 8));
        Assert.Equal(0, (bytes.Length - 16) % 16);
    }
}
