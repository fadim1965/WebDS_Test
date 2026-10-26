using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WebDS_Test.Areas.Vendor.Controllers;
using WebDS_Test.Areas.Vendor.Models;
using WebDS_Test.Data;
using WebDS_Test.Domain;
using WebDS_Test.Services;
using Xunit;

namespace WebDS_Test.Tests;

/// <summary>
/// Who may do what inside a shop. The role model shipped without a surface that could create a
/// non-owner, so these tests both pin the policy and prove the controller gates that enforce it.
/// </summary>
public class VendorRoleTests
{
    private const string OwnerId = "owner-1";
    private const string ManagerId = "manager-1";
    private const string FulfilmentId = "fulfilment-1";

    // ------------------------------------------------------------------
    // Fixtures
    // ------------------------------------------------------------------

    private static Shop Shop() => new()
    {
        Id = 1,
        Name = "Shop 1",
        Slug = "shop-1",
        OwnerUserId = OwnerId,
        Status = ShopStatus.Approved
    };

    private static ApplicationUser User(string id, string email) => new()
    {
        Id = id,
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        DisplayName = email,
        PasswordHash = "not-a-real-hash"
    };

    private static ShopService Shops(ApplicationDbContext db) =>
        new(db, Mock.Of<ISlugService>(), Mock.Of<IThemeCatalog>(), NullLogger<ShopService>.Instance);

    private static ClaimsPrincipal Principal(string userId) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ClaimTypes.Name, "tester@example.com")
        }, "test"));

    private static T SignedIn<T>(T controller, string userId) where T : Controller
    {
        var http = new DefaultHttpContext { User = Principal(userId) };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, Mock.Of<ITempDataProvider>());
        return controller;
    }

    private static async Task<ApplicationDbContext> SeededWithTeam()
    {
        var db = TestSupport.NewDb();
        db.Shops.Add(Shop());
        db.Users.AddRange(
            User(OwnerId, "owner@example.com"),
            User(ManagerId, "manager@example.com"),
            User(FulfilmentId, "packer@example.com"));
        db.ShopUsers.AddRange(
            new ShopUser { ShopId = 1, UserId = OwnerId, Role = ShopRole.Owner },
            new ShopUser { ShopId = 1, UserId = ManagerId, Role = ShopRole.Manager },
            new ShopUser { ShopId = 1, UserId = FulfilmentId, Role = ShopRole.Fulfilment });
        await db.SaveChangesAsync();
        return db;
    }

    // ------------------------------------------------------------------
    // The policy matrix
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(ShopRole.Owner, true, true, true, true, true)]
    [InlineData(ShopRole.Manager, true, true, false, false, true)]
    [InlineData(ShopRole.Fulfilment, false, false, false, false, false)]
    public void The_role_matrix_is_the_documented_one(
        ShopRole role,
        bool afterSales,
        bool financials,
        bool bank,
        bool team,
        bool customerVoice)
    {
        var access = new ShopAccess(Shop(), role);

        Assert.Equal(afterSales, access.CanDecideAfterSales());
        Assert.Equal(financials, access.CanViewFinancials());
        Assert.Equal(bank, access.CanManagePayoutDetails());
        Assert.Equal(team, access.CanManageTeam());
        Assert.Equal(customerVoice, access.CanRespondToCustomers());
    }

    [Fact]
    public void Fulfilment_staff_can_pack_but_not_touch_the_catalogue_or_settings()
    {
        var access = new ShopAccess(Shop(), ShopRole.Fulfilment);

        Assert.True(access.CanFulfilOrders);
        Assert.False(access.CanManageCatalogue);
        Assert.False(access.CanManageSettings);
    }

    [Fact]
    public void A_manager_runs_the_catalogue_but_not_the_bank()
    {
        var access = new ShopAccess(Shop(), ShopRole.Manager);

        Assert.True(access.CanManageCatalogue);
        Assert.True(access.CanFulfilOrders);
        Assert.False(access.CanManageSettings);
        Assert.False(access.CanManagePayoutDetails());
    }

    // ------------------------------------------------------------------
    // Bank destination
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_manager_cannot_change_the_bank_destination()
    {
        await using var db = await SeededWithTeam();
        var controller = SignedIn(
            new PayoutsController(Shops(db), Mock.Of<IPayoutService>()), ManagerId);

        var result = await controller.SaveBank("Manager", "Bank", "LB000000", default);

        Assert.IsType<ForbidResult>(result);
        Assert.Null((await db.Shops.AsNoTracking().FirstAsync()).BankIban);
    }

    [Fact]
    public async Task The_owner_can_change_the_bank_destination()
    {
        await using var db = await SeededWithTeam();
        var controller = SignedIn(
            new PayoutsController(Shops(db), Mock.Of<IPayoutService>()), OwnerId);

        var result = await controller.SaveBank("Owner", "Bank", "LB111111", default);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("LB111111", (await db.Shops.AsNoTracking().FirstAsync()).BankIban);
    }

    // ------------------------------------------------------------------
    // After-sales decisions
    // ------------------------------------------------------------------

    [Fact]
    public async Task Fulfilment_staff_cannot_decide_a_return()
    {
        await using var db = await SeededWithTeam();
        var returns = new Mock<IReturnService>();
        var controller = SignedIn(
            new ReturnsController(
                returns.Object,
                Shops(db),
                Mock.Of<IAuditTrail>(),
                TestSupport.Options(new StoreSettings())),
            FulfilmentId);

        var result = await controller.Decide(7, approve: true, note: null, default);

        Assert.IsType<ForbidResult>(result);
        returns.Verify(
            r => r.DecideAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task A_manager_may_decide_a_return()
    {
        await using var db = await SeededWithTeam();
        var returns = new Mock<IReturnService>();
        returns.Setup(r => r.GetAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReturnRequest { Id = 7, ShopId = 1, Reference = "RET-1" });
        returns.Setup(r => r.DecideAsync(7, true, null, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ReturnResult.Ok(new ReturnRequest { Id = 7, ShopId = 1 }, "Approved."));

        var controller = SignedIn(
            new ReturnsController(
                returns.Object,
                Shops(db),
                Mock.Of<IAuditTrail>(),
                TestSupport.Options(new StoreSettings())),
            ManagerId);

        var result = await controller.Decide(7, approve: true, note: null, default);

        Assert.IsType<RedirectToActionResult>(result);
        returns.Verify(
            r => r.DecideAsync(7, true, null, It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ------------------------------------------------------------------
    // Team management
    // ------------------------------------------------------------------

    [Fact]
    public async Task The_team_screen_is_closed_to_a_manager()
    {
        await using var db = await SeededWithTeam();
        var controller = SignedIn(new TeamController(Shops(db), db, Mock.Of<IAuditTrail>()), ManagerId);

        Assert.IsType<ForbidResult>(await controller.Index(default));
    }

    [Fact]
    public async Task The_owner_sees_every_member_of_the_team()
    {
        await using var db = await SeededWithTeam();
        var controller = SignedIn(new TeamController(Shops(db), db, Mock.Of<IAuditTrail>()), OwnerId);

        var view = Assert.IsType<ViewResult>(await controller.Index(default));
        var model = Assert.IsType<TeamIndexViewModel>(view.Model);

        Assert.Equal(3, model.Members.Count);
        Assert.Contains(model.Members, m => m.UserId == OwnerId && m.IsOwner);
        Assert.Contains(model.Members, m => m.UserId == ManagerId && !m.IsOwner);
    }

    [Fact]
    public async Task A_colleague_must_have_an_account_and_cannot_be_made_owner()
    {
        await using var db = await SeededWithTeam();
        var shops = Shops(db);

        var noAccount = await shops.AddUserAsync(1, "stranger@example.com", ShopRole.Manager);
        Assert.False(noAccount.Success);

        var asOwner = await shops.AddUserAsync(1, "manager@example.com", ShopRole.Owner);
        Assert.False(asOwner.Success);

        Assert.Equal(3, await db.ShopUsers.CountAsync());
    }

    [Fact]
    public async Task A_manager_can_be_added_once_and_then_removed()
    {
        await using var db = await SeededWithTeam();
        var shops = Shops(db);
        db.Users.Add(User("new-1", "new@example.com"));
        await db.SaveChangesAsync();

        Assert.True((await shops.AddUserAsync(1, "new@example.com", ShopRole.Manager)).Success);
        Assert.False((await shops.AddUserAsync(1, "new@example.com", ShopRole.Manager)).Success);

        Assert.True((await shops.RemoveUserAsync(1, "new-1")).Success);
        Assert.False(await db.ShopUsers.AnyAsync(u => u.UserId == "new-1"));
    }

    [Fact]
    public async Task The_owner_cannot_be_removed_from_the_team()
    {
        await using var db = await SeededWithTeam();
        var result = await Shops(db).RemoveUserAsync(1, OwnerId);

        Assert.False(result.Success);
        Assert.True(await db.ShopUsers.AnyAsync(u => u.ShopId == 1 && u.UserId == OwnerId));
    }
}
