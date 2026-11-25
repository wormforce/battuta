using System.IO;
using Battuta.Linux.Platform.Authorization;

namespace Battuta.Linux.Tests.Platform;

/// <summary>
/// The polkit unix-process subject needs the /proc starttime field; polkit
/// rejects the subject without it ("Didn't find value for key 'start-time'
/// of type 't'") — the live-run failure this parser fixes.
/// </summary>
public sealed class PolkitClientTests
{
    [Fact]
    public void ReadProcessStartTime_OwnProcess_IsPositive()
    {
        var startTime = PolkitClient.ReadProcessStartTime(Environment.ProcessId);

        Assert.True(startTime > 0, "the process started after boot");
    }

    [Fact]
    public void ReadProcessStartTime_OwnProcess_IsStableAcrossReads()
    {
        var first = PolkitClient.ReadProcessStartTime(Environment.ProcessId);
        var second = PolkitClient.ReadProcessStartTime(Environment.ProcessId);

        Assert.Equal(first, second);
    }

    [Fact]
    public void ReadProcessStartTime_OwnProcess_MatchesIndependentStatParse()
    {
        // Independent parse of the same field: /proc/<pid>/stat field 22
        // (starttime) sits after the comm field, which is wrapped in
        // parentheses; split from the LAST ')' so a ')' inside comm cannot
        // shift the field index.
        var content = File.ReadAllText($"/proc/{Environment.ProcessId}/stat");
        var after = content[(content.LastIndexOf(')') + 1)..].Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries);

        var expected = ulong.Parse(after[19]);
        var actual = PolkitClient.ReadProcessStartTime(Environment.ProcessId);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ReadProcessStartTime_InvalidPid_Throws(int pid) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PolkitClient.ReadProcessStartTime(pid));

    [Fact]
    public void ClassifyAuthorityError_UnregisteredAction_IsDeploymentFailure()
    {
        // Verbatim reply captured live from polkitd on this machine.
        var message = "org.freedesktop.PolicyKit1.Error.Failed: " +
            "Action io.github.wormforce.Battuta.Input1.subscribe is not registered";

        Assert.Equal(
            PolkitRequestFailure.ActionNotRegistered,
            PolkitClient.ClassifyAuthorityError(message));
    }

    [Theory]
    [InlineData(
        "org.freedesktop.DBus.Error.ServiceUnknown: The name " +
        "org.freedesktop.PolicyKit1 was not provided by any .service files")]
    [InlineData(
        "org.freedesktop.DBus.Error.NameHasNoOwner: Could not get owner of " +
        "name 'org.freedesktop.PolicyKit1': no such name")]
    public void ClassifyAuthorityError_MissingAuthority_IsDeploymentFailure(
        string message) =>
        Assert.Equal(
            PolkitRequestFailure.AuthorityUnavailable,
            PolkitClient.ClassifyAuthorityError(message));

    [Theory]
    [InlineData("")]
    [InlineData(
        "org.freedesktop.PolicyKit1.Error.NotAuthorized: authorization failed")]
    public void ClassifyAuthorityError_OtherErrors_AreNotDeploymentFailures(
        string message) =>
        Assert.Null(PolkitClient.ClassifyAuthorityError(message));

    [Fact]
    public void ReadProcessStartTime_MissingProcess_Throws()
    {
        // A pid that does not exist: /proc has no entry for it. High pids
        // are still far below pid_max on any realistic system.
        var missing = 4_000_000;

        Assert.ThrowsAny<SystemException>(
            () => PolkitClient.ReadProcessStartTime(missing));
    }

    /// <summary>
    /// Live regression for the start-time subject fix: the authority must
    /// parse the subject and hand back a decision (or an action-level
    /// verdict). Before the fix, real polkit rejected the message outright
    /// ("Error parsing unix-process subject: Didn't find value for key
    /// 'start-time'"), which surfaced in the GUI as a failed authorization
    /// request. Environmental outcomes — the action not being installed, or
    /// polkitd not running — skip; a subject-shaped error still fails.
    /// </summary>
    [Fact]
    public async Task CheckSubscribeAgainstRealAuthority_RoundTripsSubject()
    {
        var address = Environment.GetEnvironmentVariable("DBUS_SYSTEM_BUS_ADDRESS");
        if (string.IsNullOrWhiteSpace(address))
        {
            address = Tmds.DBus.Protocol.DBusAddress.System;
        }

        if (string.IsNullOrWhiteSpace(address))
        {
            Assert.Skip("no system D-Bus is available");
        }

        await using var client = await PolkitClient.ConnectAsync();

        PolkitDecision decision;
        try
        {
            decision = await client.CheckSubscribeAsync(interactive: false);
        }
        catch (Exception error) when (
            error is Tmds.DBus.Protocol.DBusErrorReplyException or PolkitRequestException)
        {
            // A subject-shaped rejection means the start-time subject
            // regressed — that must fail loudly. Everything else on a
            // machine without the package (unknown action, absent polkitd)
            // is environmental, not a subject regression.
            if (error.Message.Contains(
                    "unix-process", StringComparison.OrdinalIgnoreCase) ||
                error.Message.Contains(
                    "start-time", StringComparison.OrdinalIgnoreCase))
            {
                throw;
            }

            Assert.Skip($"subscribe action unavailable here: {error.Message}");
            return;
        }

        Assert.True(
            decision is PolkitDecision.Authorized
                or PolkitDecision.Challenge
                or PolkitDecision.Denied);
    }
}
