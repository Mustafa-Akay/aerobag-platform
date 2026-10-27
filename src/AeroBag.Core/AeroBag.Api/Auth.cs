using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AeroBag.Infrastructure.Security;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AeroBag.Api;

/// <summary>Roller ve jeton iddiaları (claim) için sabitler.</summary>
public static class Roles
{
    public const string Passenger = "passenger";
    public const string Ops = "ops";
    public const string Security = "security";

    public const string StaffPolicy = "staff";
    public const string SecurityPolicy = "security";

    public const string RoleClaim = "role";
    public const string SubjectClaim = "sub";
    public const string BagClaim = "bag";

    public const string Issuer = "aerobag-api";
    public const string Audience = "aerobag";
}

public sealed record StaffUser(string Username, string PasswordHash, string Role);

/// <summary>
/// Kimlik doğrulama ayarları. İmza anahtarı yapılandırmada yoksa her açılışta rastgele bir anahtar üretilir
/// (jetonlar yeniden başlatmada geçersiz olur); kodda sabit bir imza anahtarı yoktur.
/// Kullanıcılar yapılandırmada yoksa yalnızca demo kullanıcıları kullanılır (README'de yazılı demo parolaları).
/// </summary>
public sealed class AuthSettings
{
    public const int MinKeyBytes = 32;

    public required byte[] SigningKey { get; init; }
    public required bool SigningKeyIsEphemeral { get; init; }
    public required IReadOnlyDictionary<string, StaffUser> Users { get; init; }
    public required bool UsingDemoUsers { get; init; }
    public TimeSpan StaffTokenLifetime { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan PassengerTokenLifetime { get; init; } = TimeSpan.FromMinutes(30);

    // Demo parolaları: ops / ops-demo-2026, security / security-demo-2026. Yalnızca yerel demo içindir.
    private static readonly StaffUser[] DemoUsers =
    [
        new("ops", "pbkdf2-sha256$600000$A+h9Hgw50yiWLG0sbM2lqQ==$6d+uwsPTovCxPJR12QYXS+qMbRN+XiIxBNGNuooY5Uk=", Roles.Ops),
        new("security", "pbkdf2-sha256$600000$Svx/dHfDJC7sXyER3lJr8A==$v8CTuQZVnbxGJEKXynnUoRJ0jyC28H4lPb8pmvqpe8I=", Roles.Security),
    ];

    public static AuthSettings FromConfiguration(IConfiguration config)
    {
        var keyText = config["Auth:SigningKey"];
        byte[] key;
        var ephemeral = false;
        if (string.IsNullOrWhiteSpace(keyText))
        {
            key = RandomNumberGenerator.GetBytes(MinKeyBytes);
            ephemeral = true;
        }
        else
        {
            key = Encoding.UTF8.GetBytes(keyText);
            if (key.Length < MinKeyBytes)
            {
                throw new InvalidOperationException($"Auth:SigningKey en az {MinKeyBytes} bayt olmalı.");
            }
        }

        var configured = config.GetSection("Auth:Users").GetChildren()
            .Select(s => new StaffUser(s["Username"] ?? "", s["PasswordHash"] ?? "", s["Role"] ?? ""))
            .ToList();
        foreach (var u in configured)
        {
            if (string.IsNullOrWhiteSpace(u.Username) || string.IsNullOrWhiteSpace(u.PasswordHash) || u.Role is not (Roles.Ops or Roles.Security))
            {
                throw new InvalidOperationException("Auth:Users içinde eksik alan ya da geçersiz rol var (ops ya da security olmalı).");
            }
        }

        var users = configured.Count > 0 ? configured : DemoUsers.ToList();
        return new AuthSettings
        {
            SigningKey = key,
            SigningKeyIsEphemeral = ephemeral,
            Users = users.ToDictionary(u => u.Username, StringComparer.Ordinal),
            UsingDemoUsers = configured.Count == 0,
        };
    }

    public TokenValidationParameters ValidationParameters() => new()
    {
        ValidateIssuer = true,
        ValidIssuer = Roles.Issuer,
        ValidateAudience = true,
        ValidAudience = Roles.Audience,
        ValidateLifetime = true,
        RequireExpirationTime = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(SigningKey),
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ClockSkew = TimeSpan.FromSeconds(30),
        RoleClaimType = Roles.RoleClaim,
        NameClaimType = Roles.SubjectClaim,
    };
}

public sealed record IssuedToken(string AccessToken, int ExpiresInSec);

/// <summary>HS256 imzalı kısa ömürlü jeton üretir. Jetonda kişisel veri yoktur (yolcu jetonunda yalnızca bagaj etiketleri).</summary>
public sealed class TokenService(AuthSettings settings, TimeProvider clock)
{
    private readonly JsonWebTokenHandler _handler = new();
    private readonly SigningCredentials _credentials = new(new SymmetricSecurityKey(settings.SigningKey), SecurityAlgorithms.HmacSha256);

    /// <summary>Kullanıcı adı ve parola doğruysa personel jetonu, değilse null. Bilinmeyen kullanıcıda da aynı maliyette doğrulama yapılır.</summary>
    public IssuedToken? IssueStaff(string? username, string? password)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password) || username.Length > 64 || password.Length > 256)
        {
            return null;
        }

        if (!settings.Users.TryGetValue(username, out var user))
        {
            PasswordHasher.VerifyDummy(password);
            return null;
        }

        if (!PasswordHasher.Verify(password, user.PasswordHash))
        {
            return null;
        }

        return Issue([new Claim(Roles.SubjectClaim, user.Username), new Claim(Roles.RoleClaim, user.Role)], settings.StaffTokenLifetime);
    }

    /// <summary>Yolcu jetonu: yalnızca sorguda bulunan bagaj etiketlerine erişim verir.</summary>
    public IssuedToken IssuePassenger(IEnumerable<string> bagTags)
    {
        var claims = new List<Claim> { new(Roles.SubjectClaim, "passenger"), new(Roles.RoleClaim, Roles.Passenger) };
        claims.AddRange(bagTags.Distinct(StringComparer.Ordinal).Select(t => new Claim(Roles.BagClaim, t)));
        return Issue(claims, settings.PassengerTokenLifetime);
    }

    private IssuedToken Issue(IEnumerable<Claim> claims, TimeSpan lifetime)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Roles.Issuer,
            Audience = Roles.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now,
            NotBefore = now,
            Expires = now + lifetime,
            SigningCredentials = _credentials,
        });
        return new IssuedToken(token, (int)lifetime.TotalSeconds);
    }
}

public static class ClaimsPrincipalExtensions
{
    public static bool IsStaff(this ClaimsPrincipal user) => user.IsInRole(Roles.Ops) || user.IsInRole(Roles.Security);

    /// <summary>Personel tüm bagajlara, yolcu yalnızca jetonundaki bagajlara erişebilir.</summary>
    public static bool CanSeeBag(this ClaimsPrincipal user, string bagTag) =>
        user.IsStaff() || (user.IsInRole(Roles.Passenger) && user.FindAll(Roles.BagClaim).Any(c => c.Value == bagTag));
}

public sealed record LoginRequest(string? Username, string? Password);

public sealed record LookupRequest(string? Pnr, string? Surname);
