using MyOwnGames;
using Xunit;

namespace AnSAM.Tests
{
    public class InputValidatorTests
    {
        [Theory]
        [InlineData("0123456789ABCDEF0123456789ABCDE")] // 31 chars
        [InlineData("0123456789ABCDEFG0123456789ABCDE0")] // non-hex
        public void IsValidApiKey_Invalid_ReturnsFalse(string key)
        {
            Assert.False(InputValidator.IsValidApiKey(key));
        }

        [Fact]
        public void IsValidApiKey_Valid_ReturnsTrue()
        {
            Assert.True(InputValidator.IsValidApiKey("0123456789ABCDEF0123456789ABCDEF"));
        }

        [Theory]
        [InlineData("7656119801234567")] // 16 digits
        [InlineData("76561298012345678")] // wrong prefix
        [InlineData("7656119A01234567B")] // non-digit
        public void IsValidSteamId64_Invalid_ReturnsFalse(string id)
        {
            Assert.False(InputValidator.IsValidSteamId64(id));
        }

        [Fact]
        public void IsValidSteamId64_Valid_ReturnsTrue()
        {
            Assert.True(InputValidator.IsValidSteamId64("76561198012345678"));
        }
    }
}
