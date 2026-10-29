using System.Text;
using LanBridge.Core;

static void Check(bool pass, string name)
{
    if (!pass) throw new Exception("FAILED: " + name);
    Console.WriteLine("PASS " + name);
}

using var a = DeviceIdentity.CreateEphemeral();
using var b = DeviceIdentity.CreateEphemeral();
using var c = DeviceIdentity.CreateEphemeral();
using var rogue = DeviceIdentity.CreateEphemeral();
var group = GroupLedger.Create("Home LAN", a);
Check(group.IsAdmin(a.DeviceId), "creator admin");
Check(group.Members.Count == 1, "initial membership");
var roundtrip = GroupLedger.Import(group.Document);
Check(roundtrip.IsAdmin(a.DeviceId), "genesis signature verified");
Check(!roundtrip.IsActive(rogue.DeviceId), "untrusted device rejected");

// A approves B and promotes B to Admin. A can then go offline and B can approve C.
group.Issue("Joined", b.DeviceId, b.PublicCertificate, "Member", a, "PC B");
group.Issue("RoleChanged", b.DeviceId, "", "Admin", a);
Check(group.IsAdmin(b.DeviceId), "multiple admins");
group.Issue("Joined", c.DeviceId, c.PublicCertificate, "Member", b, "PC C");
Check(group.IsActive(c.DeviceId), "other admin can approve");
var remoteCopy = GroupLedger.Import(group.Document);
Check(remoteCopy.IsAdmin(b.DeviceId), "membership survives snapshot sync");

// Data tampering must fail a full log rebuild.
var forged = new GroupDocument
{
    GroupId = group.Document.GroupId, RootDeviceId = group.Document.RootDeviceId,
    Name = group.Document.Name, Events = group.Document.Events.Select(x => new MembershipEvent
    {
        EventId = x.EventId, GroupId = x.GroupId, Type = x.Type, ActorId = x.ActorId,
        SubjectId = x.SubjectId, SubjectCertificate = x.SubjectCertificate,
        SubjectName = x.SubjectName, Role = x.Role, Clock = x.Clock, Signature = x.Signature
    }).ToList()
};
forged.Events[1].Role = "Admin";
var rejected = false;
try { _ = GroupLedger.Import(forged); } catch (InvalidDataException) { rejected = true; }
Check(rejected, "tampered membership signature rejected");

rejected = false;
try { group.Issue("Joined", rogue.DeviceId, rogue.PublicCertificate, "Admin", rogue); }
catch (UnauthorizedAccessException) { rejected = true; }
Check(rejected, "non-admin cannot issue event");

group.Issue("Revoked", c.DeviceId, "", "", b);
Check(!group.IsActive(c.DeviceId), "revocation");
var other = GroupLedger.Import(remoteCopy.Document);
Check(other.Merge(group.Document), "signed merge after offline");
Check(!other.IsActive(c.DeviceId), "offline peer converges to revoke");

// Concurrent changes by two Admins must converge and must not remove the final Admin.
var branchA = GroupLedger.Import(group.Document);
var branchB = GroupLedger.Import(group.Document);
branchA.Issue("RoleChanged", b.DeviceId, "", "Member", a);
branchB.Issue("RoleChanged", a.DeviceId, "", "Member", b);
Check(branchA.Merge(branchB.Document), "concurrent signed admin edits merge");
Check(branchA.Members.Values.Count(x => x.Active && x.Role == "Admin") >= 1,
    "last-admin invariant survives concurrency");

rejected = false;
try { FileSafety.SafeName("..\\Windows\\bad.txt"); } catch (InvalidDataException) { rejected = true; }
Check(rejected, "path traversal rejected");
Check(FileSafety.SafeName("Tiếng Việt 你好.txt") == "Tiếng Việt 你好.txt", "Unicode filename");
rejected = false;
try { FileSafety.SafeName("CON.txt"); } catch (InvalidDataException) { rejected = true; }
Check(rejected, "reserved DOS device name rejected");

var first = new ClipboardItem { OriginDeviceId = a.DeviceId, Text = "một", Clock = 7 };
var second = new ClipboardItem { OriginDeviceId = b.DeviceId, Text = "hai", Clock = 8 };
Check(ClipboardOrder.IsNewer(second, first), "clipboard latest Lamport clock wins");
Check(!ClipboardOrder.IsNewer(first, second), "old clipboard ignored");

var msg = WireEnvelope.Create(a.DeviceId, "Clipboard", first);
await using var buffer = new MemoryStream();
await WireIO.SendAsync(buffer, msg, CancellationToken.None);
buffer.Position = 0;
var read = await WireIO.ReceiveAsync(buffer, CancellationToken.None);
Check(read.Read<ClipboardItem>().Text == "một", "framed protocol round trip");

Console.WriteLine("All core smoke tests passed.");
