using ArtForgeAI.Models;

namespace ArtForgeAI.Services;

public interface IAuthService
{
    Task<AppUser> FindOrCreateUserAsync(string googleId, string email, string name, string? avatar, string superAdminEmail, string? referralCode = null);
    Task<AppUser?> GetUserByIdAsync(int id);
    Task<List<AppUser>> GetAllUsersAsync();
    Task<int> GetTotalUserCountAsync();
    Task UpdateUserRoleAsync(int userId, AppRole role);
    Task SetUserActiveStatusAsync(int userId, bool isActive);
    Task<Dictionary<string, int>> GetUserRegistrationStatsAsync(int days = 30);
}
