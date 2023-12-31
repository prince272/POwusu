﻿using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using POwusu.Server.Data;
using POwusu.Server.Entities.Identity;
using POwusu.Server.Helpers;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace POwusu.Server.Extensions.Authentication
{
    public class JwtTokenManager : IJwtTokenManager
    {
        private readonly IOptions<JwtTokenOptions> _jwtTokenOptions;
        private readonly AppDbContext _appDbContext;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IUserClaimsPrincipalFactory<User> _userClaimsPrincipalFactory;

        public JwtTokenManager(IOptions<JwtTokenOptions> jwtTokenOptions, AppDbContext appDbContext, IHttpContextAccessor httpContextAccessor, IUserClaimsPrincipalFactory<User> userClaimsPrincipalFactory)
        {
            _jwtTokenOptions = jwtTokenOptions;
            _appDbContext = appDbContext;
            _httpContextAccessor = httpContextAccessor;
            _userClaimsPrincipalFactory = userClaimsPrincipalFactory;
        }

        public async Task<JwtTokenInfo> GenerateAsync(User user, CancellationToken cancellationToken = default)
        {
            if (user is null) throw new ArgumentNullException(nameof(user));

            var currentTime = DateTimeOffset.UtcNow;
            var claims = (await _userClaimsPrincipalFactory.CreateAsync(user)).Claims.ToList();
            var (accessToken, accessTokenExpiresAt) = GenerateAccessToken(claims, currentTime);
            var (refreshToken, refreshTokenExpiresAt) = GenerateRefreshToken(currentTime);

            if (!_jwtTokenOptions.Value.AllowMultipleTokens)
            {
                await _appDbContext.Set<JwtToken>().Where(_ => _.UserId == user.Id).ForEachAsync(session => _appDbContext.Remove(session), cancellationToken);
            }

            await _appDbContext.AddAsync(new JwtToken
            {
                UserId = user.Id,

                Id = Guid.NewGuid().ToString(),

                AccessTokenHash = HashHelper.GenerateSHA256Hash(accessToken),
                RefreshTokenHash = HashHelper.GenerateSHA256Hash(refreshToken),

                AccessTokenExpiresAt = accessTokenExpiresAt,
                RefreshTokenExpiresAt = refreshTokenExpiresAt
            }, cancellationToken);

            await _appDbContext.SaveChangesAsync(cancellationToken);

            return new JwtTokenInfo
            {
                AccessToken = accessToken,
                AccessTokenExpiresAt = accessTokenExpiresAt,
                RefreshToken = refreshToken,
                RefreshTokenExpiresAt = refreshTokenExpiresAt
            };
        }

        private (string AccessToken, DateTimeOffset AccessTokenExpiresAt) GenerateAccessToken(ICollection<Claim> claims, DateTimeOffset currentTime)
        {
            if (claims == null) throw new ArgumentNullException(nameof(claims));

            var httpContext = _httpContextAccessor.HttpContext ?? throw new InvalidOperationException("Unable to determine the current HttpContext.");
            var issuer = string.Concat(httpContext.Request.Scheme, "://", httpContext.Request.Host.ToUriComponent()).ToLower();
            var audience = httpContext.Request.Headers.Referer.ToString();
            audience = !string.IsNullOrEmpty(audience) ? audience : issuer;

            claims.Add(new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString(), ClaimValueTypes.String, issuer));
            claims.Add(new(JwtRegisteredClaimNames.Iss, issuer, ClaimValueTypes.String, issuer));
            claims.Add(new(JwtRegisteredClaimNames.Iat, currentTime.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64, issuer));

            var expiresAt = currentTime.Add(_jwtTokenOptions.Value.AccessTokenExpiresIn);

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtTokenOptions.Value.Secret));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
            var token = new JwtSecurityToken(issuer, audience, claims, currentTime.DateTime, expiresAt.DateTime, creds);
            var tokenValue = new JwtSecurityTokenHandler().WriteToken(token);
            return (tokenValue, expiresAt);
        }

        private (string RefreshToken, DateTimeOffset RefreshTokenExpiresAt) GenerateRefreshToken(DateTimeOffset currentTime)
        {
            var httpContext = _httpContextAccessor.HttpContext ?? throw new InvalidOperationException("Unable to determine the current HttpContext.");
            var issuer = string.Concat(httpContext.Request.Scheme, "://", httpContext.Request.Host.ToUriComponent()).ToLower();
            var audience = httpContext.Request.Headers.Referer.ToString();
            audience = !string.IsNullOrEmpty(audience) ? audience : issuer;

            var claims = new Claim[]
            {
                new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString(), ClaimValueTypes.String, issuer),
                new(JwtRegisteredClaimNames.Iss, issuer, ClaimValueTypes.String, issuer),
                new(JwtRegisteredClaimNames.Iat, currentTime.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64, issuer),
            };

            var expiresAt = currentTime.Add(_jwtTokenOptions.Value.RefreshTokenExpiresIn);

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtTokenOptions.Value.Secret));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
            var token = new JwtSecurityToken(issuer, audience, claims, currentTime.DateTime, expiresAt.DateTime, creds);
            var tokenValue = new JwtSecurityTokenHandler().WriteToken(token);
            return (tokenValue, expiresAt);
        }

        public async Task<User?> FindUserByTokenAsync(string token, CancellationToken cancellationToken = default)
        {
            if (token == null) throw new ArgumentNullException(nameof(token));

            var tokenHash = HashHelper.GenerateSHA256Hash(token);

            var tokenObject = await _appDbContext.Set<JwtToken>().FirstOrDefaultAsync(_ => _.AccessTokenHash == tokenHash || _.RefreshTokenHash == tokenHash, cancellationToken);

            if (tokenObject == null || tokenObject.RefreshTokenExpiresAt < DateTimeOffset.UtcNow) return null;
            return await _appDbContext.FindAsync<User>(keyValues: new object[] { tokenObject.UserId }, cancellationToken);
        }

        public async Task InvalidateAsync(User user, string token, CancellationToken cancellationToken = default)
        {
            if (!_jwtTokenOptions.Value.AllowMultipleTokens)
            {
                await _appDbContext.Set<JwtToken>().Where(_ => _.UserId == user.Id).ForEachAsync(session => _appDbContext.Remove(session), cancellationToken);
            }
            else
            {
                var tokenHash = HashHelper.GenerateSHA256Hash(token);
                var currentTime = DateTimeOffset.UtcNow;

                await _appDbContext.Set<JwtToken>()
                    .Where(_ => _.UserId == user.Id)
                    .Where(_ => _.AccessTokenHash == tokenHash || _.RefreshTokenHash == tokenHash)
                    .Where(_ => _.AccessTokenExpiresAt < currentTime)
                    .ForEachAsync(session => _appDbContext.Remove(session), cancellationToken);
            }

            await _appDbContext.SaveChangesAsync(cancellationToken);
        }

        public async Task<bool> ValidateAsync(string token, CancellationToken cancellationToken = default)
        {
            if (token == null) throw new ArgumentNullException(nameof(token));

            var tokenHash = HashHelper.GenerateSHA256Hash(token);

            var tokenObject = await _appDbContext.Set<JwtToken>().FirstOrDefaultAsync(_ => _.AccessTokenHash == tokenHash || _.RefreshTokenHash == tokenHash, cancellationToken);

            return tokenObject != null && tokenObject.RefreshTokenExpiresAt >= DateTimeOffset.UtcNow;
        }
    }

    public interface IJwtTokenManager
    {
        Task<JwtTokenInfo> GenerateAsync(User user, CancellationToken cancellationToken = default);

        Task InvalidateAsync(User user, string token, CancellationToken cancellationToken = default);

        Task<bool> ValidateAsync(string token, CancellationToken cancellationToken = default);

        Task<User?> FindUserByTokenAsync(string token, CancellationToken cancellationToken = default);
    }
}
