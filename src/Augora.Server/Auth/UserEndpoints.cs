using System.Security.Claims;
using Augora.Contracts;
using Augora.Server.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Augora.Server.Auth;

internal static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var users = app.MapGroup("/api/users").WithTags("Users").RequireAuthorization(Roles.Admin);

        users.MapGet("/", async (UserManager<AppUser> manager) =>
        {
            var admins = (await manager.GetUsersInRoleAsync(Roles.Admin)).Select(u => u.Id).ToHashSet();
            return (await manager.Users.OrderBy(u => u.UserName).ToListAsync())
                .Select(u => new UserDto(u.Id, u.UserName ?? "", u.DisplayName, admins.Contains(u.Id)))
                .ToList();
        });

        users.MapPost("/", async Task<Results<Created<UserDto>, ValidationProblem>> (
            CreateUserRequest request, UserManager<AppUser> manager, RoleManager<IdentityRole> roles) =>
        {
            var user = new AppUser { UserName = request.UserName.Trim(), DisplayName = request.DisplayName?.Trim() };
            var result = await manager.CreateAsync(user, request.Password);
            if (!result.Succeeded)
            {
                return AuthEndpoints.ValidationProblem(result);
            }

            if (request.IsAdmin)
            {
                await AuthEndpoints.EnsureAdminAsync(user, manager, roles);
            }

            return TypedResults.Created($"/api/users/{user.Id}", await AuthEndpoints.ToDtoAsync(user, manager));
        });

        users.MapPut("/{id}", async Task<Results<Ok<UserDto>, NotFound, ProblemHttpResult, ValidationProblem>> (
            string id, UpdateUserRequest request, ClaimsPrincipal principal, UserManager<AppUser> manager, RoleManager<IdentityRole> roles) =>
        {
            if (await manager.FindByIdAsync(id) is not { } user)
            {
                return TypedResults.NotFound();
            }

            // Checked up front so nothing else in this request has been applied when it is refused.
            if (request.IsAdmin == false && manager.GetUserId(principal) == user.Id && await manager.IsInRoleAsync(user, Roles.Admin)
                && !await IsLastAdminAsync(user, manager))
            {
                return AuthEndpoints.Problem(ErrorCodes.CannotChangeSelf, "You cannot remove your own administrator role; ask another administrator.", StatusCodes.Status409Conflict);
            }

            if (request.DisplayName is not null)
            {
                user.DisplayName = request.DisplayName.Trim();
                await manager.UpdateAsync(user);
            }

            if (request.IsAdmin is { } isAdmin && isAdmin != await manager.IsInRoleAsync(user, Roles.Admin))
            {
                if (isAdmin)
                {
                    await AuthEndpoints.EnsureAdminAsync(user, manager, roles);
                }
                else if (await IsLastAdminAsync(user, manager))
                {
                    return AuthEndpoints.Problem(ErrorCodes.LastAdmin, "The last administrator cannot be demoted.", StatusCodes.Status409Conflict);
                }
                else
                {
                    await manager.RemoveFromRoleAsync(user, Roles.Admin);
                }
            }

            if (!string.IsNullOrEmpty(request.NewPassword))
            {
                var reset = await manager.ResetPasswordAsync(user, await manager.GeneratePasswordResetTokenAsync(user), request.NewPassword);
                if (!reset.Succeeded)
                {
                    return AuthEndpoints.ValidationProblem(reset);
                }
            }

            return TypedResults.Ok(await AuthEndpoints.ToDtoAsync(user, manager));
        });

        users.MapDelete("/{id}", async Task<Results<NoContent, NotFound, ProblemHttpResult>> (string id, ClaimsPrincipal principal, UserManager<AppUser> manager) =>
        {
            if (await manager.FindByIdAsync(id) is not { } user)
            {
                return TypedResults.NotFound();
            }

            if (await IsLastAdminAsync(user, manager))
            {
                return AuthEndpoints.Problem(ErrorCodes.LastAdmin, "The last administrator cannot be deleted.", StatusCodes.Status409Conflict);
            }

            if (manager.GetUserId(principal) == user.Id)
            {
                return AuthEndpoints.Problem(ErrorCodes.CannotChangeSelf, "You cannot delete your own account; ask another administrator.", StatusCodes.Status409Conflict);
            }

            await manager.DeleteAsync(user);
            return TypedResults.NoContent();
        });

        var tokens = app.MapGroup("/api/tokens").WithTags("API tokens");

        tokens.MapGet("/", (ClaimsPrincipal principal, UserManager<AppUser> manager, ApiTokenService service, CancellationToken ct) =>
            service.GetForUserAsync(manager.GetUserId(principal)!, ct));

        tokens.MapPost("/", async Task<Results<Ok<CreatedApiTokenDto>, ValidationProblem>> (
            CreateApiTokenRequest request, ClaimsPrincipal principal, UserManager<AppUser> manager, ApiTokenService service, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.Name)] = ["Name is required."] });
            }

            return TypedResults.Ok(await service.CreateAsync(manager.GetUserId(principal)!, request.Name, ct));
        });

        tokens.MapDelete("/{id:guid}", async Task<Results<NoContent, NotFound>> (
            Guid id, ClaimsPrincipal principal, UserManager<AppUser> manager, ApiTokenService service, CancellationToken ct) =>
            await service.RevokeAsync(manager.GetUserId(principal)!, id, ct) ? TypedResults.NoContent() : TypedResults.NotFound());

        return app;
    }

    private static async Task<bool> IsLastAdminAsync(AppUser user, UserManager<AppUser> manager) =>
        await manager.IsInRoleAsync(user, Roles.Admin) && (await manager.GetUsersInRoleAsync(Roles.Admin)).Count == 1;
}
