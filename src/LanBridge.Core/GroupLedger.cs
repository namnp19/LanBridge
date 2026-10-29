namespace LanBridge.Core;

/// <summary>
/// Verified, deterministic, signed group membership log. Client MUST verify the root group
/// identity and intended joining-admin certificate out of band during pairing.
/// </summary>
public sealed class GroupLedger
{
    public GroupDocument Document { get; private set; }
    public Dictionary<string, Member> Members { get; private set; } = new(StringComparer.Ordinal);
    public long Clock => Document.Events.Count == 0 ? 0 : Document.Events.Max(x => x.Clock);
    public bool IsActive(string id) => Members.TryGetValue(id, out var m) && m.Active;
    public bool IsAdmin(string id) => Members.TryGetValue(id, out var m) && m.Active && m.Role == "Admin";

    private GroupLedger(GroupDocument group) { Document = group; }

    public static GroupLedger Create(string name, DeviceIdentity owner)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Group name is required");
        var document = new GroupDocument
        {
            GroupId = Guid.NewGuid().ToString("N"), Name = name.Trim()[..Math.Min(60, name.Trim().Length)],
            RootDeviceId = owner.DeviceId
        };
        var ledger = new GroupLedger(document);
        ledger.Issue("Created", owner.DeviceId, owner.PublicCertificate, "Admin", owner, Environment.MachineName);
        return ledger;
    }

    public static GroupLedger Import(GroupDocument incoming)
    {
        if (string.IsNullOrWhiteSpace(incoming.GroupId) || incoming.Events.Count is 0 or > 10000)
            throw new InvalidDataException("Invalid group data");
        var copy = new GroupDocument { GroupId = incoming.GroupId, Name = incoming.Name,
            RootDeviceId = incoming.RootDeviceId, Events = incoming.Events.ToList() };
        var result = new GroupLedger(copy);
        result.Rebuild();
        return result;
    }

    private static int Compare(MembershipEvent a, MembershipEvent b)
    {
        var clock = a.Clock.CompareTo(b.Clock);
        if (clock != 0) return clock;
        // Concurrent revocation is ordered last so it wins over grant/role change.
        var priA = a.Type == "Revoked" ? 1 : 0;
        var priB = b.Type == "Revoked" ? 1 : 0;
        return priA != priB ? priA.CompareTo(priB) : string.CompareOrdinal(a.EventId, b.EventId);
    }

    public void Rebuild()
    {
        var events = Document.Events.ToList();
        events.Sort(Compare);
        var members = new Dictionary<string, Member>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long currentClock = -1;
        Dictionary<string, Member> authorityAtClock = new(StringComparer.Ordinal);
        if (events.Count > 10000 || events.Count == 0) throw new InvalidDataException("No valid signed membership chain");
        foreach (var item in events)
        {
            if (item.Clock != currentClock)
            {
                currentClock = item.Clock;
                // Events with equal Lamport clocks are concurrent: authorize against the
                // state at the start of their shared clock, not against arbitrary tie order.
                authorityAtClock = new Dictionary<string, Member>(members, StringComparer.Ordinal);
            }
            if (item.GroupId != Document.GroupId || string.IsNullOrWhiteSpace(item.EventId) || !seen.Add(item.EventId)
                || item.Clock < 1 || item.Clock > 10_000_000_000 || item.SubjectId.Length != 64 || item.ActorId.Length != 64)
                throw new InvalidDataException("Invalid membership event");
            if (item.Type == "Created")
            {
                if (members.Count > 0 || !ReferenceEquals(item, events[0]) || item.ActorId != Document.RootDeviceId
                    || item.SubjectId != item.ActorId || item.Role != "Admin")
                    throw new InvalidDataException("Invalid genesis");
                using var rootCert = DeviceIdentity.ImportPublic(item.SubjectCertificate);
                if (DeviceIdentity.IdentityOf(rootCert) != item.SubjectId ||
                    !DeviceIdentity.Verify(item.SubjectCertificate, item.Canonical(), item.Signature))
                    throw new InvalidDataException("Invalid genesis signature");
                members.Add(item.SubjectId, new Member(item.SubjectId, item.SubjectCertificate, "Admin", true, item.SubjectName));
                continue;
            }
            if (!authorityAtClock.TryGetValue(item.ActorId, out var admin) || !admin.Active || admin.Role != "Admin"
                || !DeviceIdentity.Verify(admin.CertificateBase64, item.Canonical(), item.Signature))
                throw new InvalidDataException("Unauthorized/invalid membership signature");
            switch (item.Type)
            {
                case "Joined":
                    if (item.Role is not ("Admin" or "Member") || string.IsNullOrWhiteSpace(item.SubjectCertificate))
                        throw new InvalidDataException("Invalid join payload");
                    using (var cert = DeviceIdentity.ImportPublic(item.SubjectCertificate))
                    {
                        if (DeviceIdentity.IdentityOf(cert) != item.SubjectId)
                            throw new InvalidDataException("Public key does not match subject identity");
                    }
                    // A revoked device may rejoin only through a new explicit signed join event.
                    members[item.SubjectId] = new Member(item.SubjectId, item.SubjectCertificate, item.Role, true, item.SubjectName);
                    break;
                case "RoleChanged":
                    if (item.Role is not ("Admin" or "Member") || !authorityAtClock.TryGetValue(item.SubjectId, out var oldAtClock) || !oldAtClock.Active)
                        throw new InvalidDataException("Cannot change missing or already revoked member");
                    if (!members.TryGetValue(item.SubjectId, out var old) || !old.Active) break; // concurrent revoke wins
                    if (old.Role == "Admin" && item.Role == "Member" && members.Values.Count(x => x.Active && x.Role == "Admin") <= 1)
                        break; // preserve at least one Admin when concurrent changes conflict
                    members[item.SubjectId] = old with { Role = item.Role };
                    break;
                case "Revoked":
                    if (!authorityAtClock.TryGetValue(item.SubjectId, out var wasPresent) || !wasPresent.Active)
                        throw new InvalidDataException("Cannot revoke absent member");
                    if (!members.TryGetValue(item.SubjectId, out var target) || !target.Active) break; // concurrent duplicate revoke
                    if (target.Role == "Admin" && members.Values.Count(x => x.Active && x.Role == "Admin") <= 1)
                        break; // leave one Admin alive even under concurrent demotions/revokes
                    members[item.SubjectId] = target with { Active = false };
                    break;
                default: throw new InvalidDataException("Unknown membership operation");
            }
        }
        Members = members;
    }

    public MembershipEvent Issue(string type, string subjectId, string certificate, string role,
        DeviceIdentity actor, string subjectName = "")
    {
        if (type != "Created" && !IsAdmin(actor.DeviceId)) throw new UnauthorizedAccessException("Admin required");
        if (type is "Revoked" or "RoleChanged")
        {
            if (!Members.TryGetValue(subjectId, out var existing) || !existing.Active)
                throw new InvalidOperationException("Member is absent or already revoked");
            if (existing.Role == "Admin" && (type == "Revoked" || role == "Member") &&
                Members.Values.Count(x => x.Active && x.Role == "Admin") <= 1)
                throw new InvalidOperationException("Cannot remove the last admin");
        }
        var evt = new MembershipEvent
        {
            GroupId = Document.GroupId, Type = type, ActorId = actor.DeviceId,
            SubjectId = subjectId, SubjectCertificate = certificate,
            Role = role, SubjectName = subjectName, Clock = Clock + 1
        };
        evt.Signature = actor.Sign(evt.Canonical());
        var snapshot = Document.Events.ToList();
        Document.Events.Add(evt);
        try { Rebuild(); }
        catch { Document.Events = snapshot; Rebuild(); throw; }
        return evt;
    }

    public bool Merge(GroupDocument remote)
    {
        if (remote.GroupId != Document.GroupId || remote.RootDeviceId != Document.RootDeviceId)
            throw new InvalidDataException("Conflicting group identity");
        // Never trust any state or role from the sender: replay the merged signed event log.
        var existing = Document.Events.ToDictionary(x => x.EventId);
        foreach (var e in remote.Events)
        {
            if (existing.TryGetValue(e.EventId, out var old) &&
                (old.Canonical() != e.Canonical() || old.Signature != e.Signature))
                throw new InvalidDataException("Conflicting event ID");
        }
        var additions = remote.Events.Where(x => !existing.ContainsKey(x.EventId)).ToList();
        if (additions.Count == 0) return false;
        var combined = new GroupDocument
        {
            GroupId = Document.GroupId, RootDeviceId = Document.RootDeviceId, Name = Document.Name,
            Events = Document.Events.Concat(additions).ToList()
        };
        var checkedLedger = Import(combined);
        Document = checkedLedger.Document;
        Members = checkedLedger.Members;
        return true;
    }
}
