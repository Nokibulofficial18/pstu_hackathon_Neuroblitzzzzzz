using NCash.Domain.Entities;

namespace NCash.Application.Contracts.Security;

public interface IPasswordHasher
{
    string HashPassword(string password);
    bool VerifyPassword(string password, string passwordHash);
}

public interface IJwtTokenGenerator
{
    string GenerateToken(User user, Account? account);
}
