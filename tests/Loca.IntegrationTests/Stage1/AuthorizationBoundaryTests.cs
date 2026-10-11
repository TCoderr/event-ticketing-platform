using System.Net;
using Loca.IntegrationTests.Infrastructure;
using Npgsql;

namespace Loca.IntegrationTests.Stage1;

public sealed class AuthorizationBoundaryTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    [Fact]
    public async Task Registration_cannot_mass_assign_platform_admin()
    {
        var client = Factory.NewClient();
        await client.FetchCsrfAsync();

        (await client.PostAsync("/api/auth/register", """{"email":"hirsli@loca.test","password":"Deneme-Parola-2026","displayName":"Hırslı","isPlatformAdmin":true}"""))
            .ShouldFail(HttpStatusCode.BadRequest, "unknown_field");
        Assert.Equal(0, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM identity.users"));

        await client.RegisterAsync("normal@loca.test");
        Assert.Equal(0, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM identity.users WHERE is_platform_admin"));
    }

    [Fact]
    public async Task Organizer_cannot_reach_platform_admin_operations_or_approve_their_own_organization()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne", approve: false);

        (await organizer.GetAsync("/api/admin/organizations")).ShouldFail(HttpStatusCode.Forbidden, "forbidden");
        (await organizer.PostAsync($"/api/admin/organizations/{organizationId}/approve")).ShouldFail(HttpStatusCode.Forbidden, "forbidden");

        var status = await Database.ScalarAsAdminAsync<string>("SELECT status FROM identity.organizations WHERE id = @id", new NpgsqlParameter("id", organizationId));
        Assert.Equal("PendingApproval", status);
    }

    [Fact]
    public async Task Member_role_cannot_be_set_to_anything_but_the_two_organization_roles()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        await Factory.NewClient().RegisterAsync("hedef@loca.test");

        (await organizer.PostAsync($"/api/organizer/organizations/{organizationId}/members", """{"email":"hedef@loca.test","role":"PlatformAdmin"}"""))
            .ShouldBe(HttpStatusCode.BadRequest);
        (await organizer.PostAsync($"/api/organizer/organizations/{organizationId}/members", """{"email":"hedef@loca.test","role":7}"""))
            .ShouldBe(HttpStatusCode.BadRequest);
        (await organizer.PostAsync($"/api/organizer/organizations/{organizationId}/members", """{"email":"hedef@loca.test","role":"GateStaff","isPlatformAdmin":true}"""))
            .ShouldFail(HttpStatusCode.BadRequest, "unknown_field");

        Assert.Equal(1, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM identity.organization_members"));
        Assert.Equal(0, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM identity.users WHERE is_platform_admin"));
    }

    [Fact]
    public async Task Organizer_cannot_read_or_change_another_organization()
    {
        var (organizerA, _, organizationA) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var (organizerB, _, organizationB) = await OrganizerAsync("org-b@loca.test", "Martı Etkinlik");
        await Factory.NewClient().RegisterAsync("hedef@loca.test");

        // Varlık sızdırılmaz: başka organizasyon "yok" gibi görünür.
        (await organizerA.GetAsync($"/api/organizer/organizations/{organizationB}")).ShouldFail(HttpStatusCode.NotFound, "not_found");
        (await organizerA.PutAsync($"/api/organizer/organizations/{organizationB}", new { name = "Ele Geçirildi" })).ShouldFail(HttpStatusCode.NotFound, "not_found");
        (await organizerA.GetAsync($"/api/organizer/organizations/{organizationB}/members")).ShouldFail(HttpStatusCode.NotFound, "not_found");
        (await organizerA.PostAsync($"/api/organizer/organizations/{organizationB}/members", new { email = "hedef@loca.test", role = "OrganizerAdmin" }))
            .ShouldFail(HttpStatusCode.NotFound, "not_found");
        (await organizerA.PostAsync($"/api/organizer/organizations/{organizationB}/members", new { email = "org-a@loca.test", role = "OrganizerAdmin" }))
            .ShouldFail(HttpStatusCode.NotFound, "not_found");

        var mine = (await organizerA.GetAsync("/api/organizer/organizations")).ShouldBe(HttpStatusCode.OK);
        Assert.Equal(organizationA, Assert.Single(mine.Json.AsArray())!["id"]!.GetValue<Guid>());

        Assert.Equal("Martı Etkinlik", await Database.ScalarAsAdminAsync<string>("SELECT name FROM identity.organizations WHERE id = @id", new NpgsqlParameter("id", organizationB)));
        Assert.Equal(1, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM identity.organization_members WHERE organization_id = @id", new NpgsqlParameter("id", organizationB)));

        // B kendi organizasyonunu yönetebilir.
        (await organizerB.PutAsync($"/api/organizer/organizations/{organizationB}", new { name = "Martı Etkinlik A.Ş." })).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Gate_staff_member_cannot_administer_the_organization()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var (gate, gateUserId) = await SignedInAsync("kapi@loca.test");
        (await organizer.PostAsync($"/api/organizer/organizations/{organizationId}/members", new { email = "kapi@loca.test", role = "GateStaff" }))
            .ShouldBe(HttpStatusCode.OK);

        (await gate.GetAsync($"/api/organizer/organizations/{organizationId}")).ShouldBe(HttpStatusCode.OK);
        (await gate.PutAsync($"/api/organizer/organizations/{organizationId}", new { name = "Kapıdan Değişti" })).ShouldFail(HttpStatusCode.Forbidden, "forbidden");
        (await gate.GetAsync($"/api/organizer/organizations/{organizationId}/members")).ShouldFail(HttpStatusCode.Forbidden, "forbidden");
        (await gate.PostAsync($"/api/organizer/organizations/{organizationId}/members", new { email = "kapi@loca.test", role = "OrganizerAdmin" }))
            .ShouldFail(HttpStatusCode.Forbidden, "forbidden");

        var role = await Database.ScalarAsAdminAsync<string>("SELECT role FROM identity.organization_members WHERE user_id = @id", new NpgsqlParameter("id", gateUserId));
        Assert.Equal("GateStaff", role);
    }

    [Fact]
    public async Task Membership_removal_takes_effect_on_the_next_request_without_a_new_login()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var (second, secondId) = await SignedInAsync("ikinci@loca.test");
        (await organizer.PostAsync($"/api/organizer/organizations/{organizationId}/members", new { email = "ikinci@loca.test", role = "OrganizerAdmin" }))
            .ShouldBe(HttpStatusCode.OK);
        (await second.GetAsync($"/api/organizer/organizations/{organizationId}/members")).ShouldBe(HttpStatusCode.OK);

        (await organizer.DeleteAsync($"/api/organizer/organizations/{organizationId}/members/{secondId}")).ShouldBe(HttpStatusCode.NoContent);

        (await second.GetAsync($"/api/organizer/organizations/{organizationId}/members")).ShouldFail(HttpStatusCode.NotFound, "not_found");
        (await second.PutAsync($"/api/organizer/organizations/{organizationId}", new { name = "Geri Geldim" })).ShouldFail(HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Last_organizer_admin_cannot_be_removed_or_demoted()
    {
        var (organizer, userId, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");

        (await organizer.DeleteAsync($"/api/organizer/organizations/{organizationId}/members/{userId}"))
            .ShouldFail(HttpStatusCode.Conflict, "last_admin_cannot_leave");
        (await organizer.PostAsync($"/api/organizer/organizations/{organizationId}/members", new { email = "org-a@loca.test", role = "GateStaff" }))
            .ShouldFail(HttpStatusCode.Conflict, "last_admin_cannot_leave");
    }

    [Fact]
    public async Task Platform_admin_moderates_organizations_and_transitions_are_guarded()
    {
        var (_, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne", approve: false);
        var admin = await SignedInPlatformAdminAsync();

        var pending = (await admin.GetAsync("/api/admin/organizations?status=PendingApproval")).ShouldBe(HttpStatusCode.OK);
        Assert.Single(pending.Json.AsArray());

        (await admin.PostAsync($"/api/admin/organizations/{organizationId}/suspend")).ShouldFail(HttpStatusCode.Conflict, "invalid_state_transition");
        (await admin.PostAsync($"/api/admin/organizations/{organizationId}/approve")).ShouldBe(HttpStatusCode.OK);
        (await admin.PostAsync($"/api/admin/organizations/{organizationId}/approve")).ShouldFail(HttpStatusCode.Conflict, "invalid_state_transition");
        (await admin.PostAsync($"/api/admin/organizations/{organizationId}/suspend")).ShouldBe(HttpStatusCode.OK);
        (await admin.PostAsync($"/api/admin/organizations/{organizationId}/reinstate")).ShouldBe(HttpStatusCode.OK);

        var audited = await Database.ScalarAsAdminAsync<long>(
            "SELECT count(*) FROM ops.audit_log WHERE entity_id = @id AND action IN ('organization.approved','organization.suspended','organization.reinstated')",
            new NpgsqlParameter("id", organizationId.ToString("D")));
        Assert.Equal(3, audited);
    }

    [Fact]
    public async Task Unknown_api_route_returns_json_404_and_errors_carry_a_correlation_id()
    {
        var client = Factory.NewClient();
        var response = await client.GetAsync("/api/does-not-exist");

        response.ShouldFail(HttpStatusCode.NotFound, "not_found");
        Assert.False(string.IsNullOrEmpty(response.Json["correlationId"]?.GetValue<string>()));
        Assert.Equal(response.Json["correlationId"]!.GetValue<string>(), response.Header("X-Correlation-Id"));
    }
}
