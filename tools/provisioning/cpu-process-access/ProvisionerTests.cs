using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SqlObserver.CpuProcessAccess
{
    // Compiled with the source only by Test-Provisioner.ps1. No native API calls.
    public static class ProvisionerTests
    {
        private static int checks;
        private static readonly SecurityIdentifier Monitor = new SecurityIdentifier("S-1-5-21-111-222-333-1001");
        private static readonly SecurityIdentifier Other = new SecurityIdentifier("S-1-5-21-111-222-333-1002");
        private static readonly SecurityIdentifier SystemSid = new SecurityIdentifier("S-1-5-18");
        private static void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); checks++; }
        private static void Refuses(Action action, string name) { bool refused = false; try { action(); } catch (InvalidOperationException) { refused = true; } Check(refused, name); }
        private static AclSnapshot Sample()
        {
            var acl = new RawAcl(2, 3);
            acl.InsertAce(0, new CommonAce(AceFlags.None, AceQualifier.AccessDenied, 1, Other, false, null));
            acl.InsertAce(1, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, 0x1fffff, SystemSid, false, null));
            acl.InsertAce(2, new CommonAce(AceFlags.Inherited, AceQualifier.AccessAllowed, 0x1000, Other, false, null));
            return new AclSnapshot(ControlFlags.SelfRelative | ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected, acl);
        }
        private static Policy Policy() { return new Policy { SqlService = "MSSQLSERVER", SqlImagePath = @"C:\Reviewed\sqlservr.exe", SqlImageSha256 = new string('a', 64), SqlAccountSid = "S-1-5-80-1-2-3-4-5", CollectorService = "SqlObserverCollector", MonitorSid = Monitor.Value }; }
        public static int Run(string temporaryDirectory)
        {
            checks = 0;
            AclSnapshot before = Sample(), after = AclRules.Add(before, Monitor);
            Check(after.Acl.Count == 4, "exactly one added ACE");
            var owned = (CommonAce)after.Acl[2];
            Check(owned.AccessMask == 0x101000 && owned.AceFlags == AceFlags.None && owned.SecurityIdentifier.Equals(Monitor), "exact rights and no inheritance");
            Check(after.Flags == before.Flags, "control flags preserved");
            Check(AclRules.Equal(AclRules.AceBytes(before.Acl[0]), AclRules.AceBytes(after.Acl[0])) && AclRules.Equal(AclRules.AceBytes(before.Acl[1]), AclRules.AceBytes(after.Acl[1])) && AclRules.Equal(AclRules.AceBytes(before.Acl[2]), AclRules.AceBytes(after.Acl[3])), "original bytes and relative order preserved");
            Check(AclRules.RemoveOwned(after, Monitor).Digest == before.Digest, "exact add/remove round trip");
            Refuses(delegate { AclRules.Add(after, Monitor); }, "preexisting monitor ACE not adopted");
            var driftAcl = new RawAcl(after.Bytes(), 4);
            driftAcl.InsertAce(2, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, 0x400, Other, false, null));
            var drift = new AclSnapshot(after.Flags, driftAcl);
            var removed = AclRules.RemoveOwned(drift, Monitor);
            Check(removed.Acl.Count == 4 && ((CommonAce)removed.Acl[2]).AccessMask == 0x400, "unrelated concurrent addition preserved on removal");
            Check(removed.Digest != before.Digest, "removal does not restore stale whole DACL");
            var duplicate = new RawAcl(after.Bytes(), 4); duplicate.InsertAce(2, AclRules.OwnedAce(Monitor));
            Refuses(delegate { AclRules.RemoveOwned(new AclSnapshot(after.Flags, duplicate), Monitor); }, "duplicate owned ACE refused");
            var broadened = new RawAcl(before.Bytes(), 4); broadened.InsertAce(2, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, 0x101010, Monitor, false, null));
            Refuses(delegate { AclRules.RemoveOwned(new AclSnapshot(before.Flags, broadened), Monitor); }, "changed rights refused rather than removed");
            Refuses(delegate { AclRules.RemoveOwned(before, Monitor); }, "missing ACE refuses guessed removal");
            var noncanonical = new RawAcl(2, 2); noncanonical.InsertAce(0, before.Acl[1]); noncanonical.InsertAce(1, before.Acl[0]);
            Refuses(delegate { AclRules.Add(new AclSnapshot(before.Flags, noncanonical), Monitor); }, "noncanonical order refused");
            Refuses(delegate { AclRules.Add(new AclSnapshot(before.Flags, null), Monitor); }, "null DACL refused");
            Refuses(delegate { AclRules.Add(new AclSnapshot(before.Flags | ControlFlags.DiscretionaryAclAutoInherited, before.Acl), Monitor); }, "unsupported controls refused");
            var callback = new RawAcl(2, 1); callback.InsertAce(0, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, 1, Other, true, new byte[0]));
            Refuses(delegate { AclRules.Add(new AclSnapshot(before.Flags, callback), Monitor); }, "callback ACE refused");
            Check(AclSnapshot.Decode(before.Bytes()).Digest == before.Digest, "binary snapshot round trip");
            byte[] trailing = new byte[before.Bytes().Length + 1]; Array.Copy(before.Bytes(), trailing, before.Bytes().Length);
            Refuses(delegate { AclSnapshot.Decode(trailing); }, "trailing snapshot bytes refused");
            var policy = Policy(); policy.Validate(); Check(policy.Binding.Length == 64, "valid exact policy");
            var broad = Policy(); broad.MonitorSid = "S-1-5-32-544";
            Refuses(delegate { broad.Validate(); }, "administrator group grant refused");
            var shared = Policy(); shared.SqlAccountSid = shared.MonitorSid;
            Refuses(delegate { shared.Validate(); }, "shared SQL and Collector identity refused");
            var wildcard = Policy(); wildcard.SqlService = "MSSQL*";
            Refuses(delegate { wildcard.Validate(); }, "wildcard service refused");
            var remote = Policy(); remote.SqlImagePath = @"\\server\share\sqlservr.exe";
            Refuses(delegate { remote.Validate(); }, "remote executable refused");
            string path = Path.Combine(temporaryDirectory, "ownership-test.bin");
            var record = new Record { Binding = policy.Binding, Pid = 42, Creation = 12345678, Before = before, After = after };
            Check(record.ReconcileState(before) == "pending-preimage", "crash before native write can retry exact preimage");
            Check(record.ReconcileState(after) == "grant-verified", "crash after native write recognizes exact postimage");
            Refuses(delegate { record.ReconcileState(drift); }, "same-generation unrelated drift refuses reconciliation");
            record.Phase = 1;
            Refuses(delegate { record.ReconcileState(before); }, "administrator removal of applied grant does not trigger regrant");
            record.Phase = 2;
            Refuses(delegate { record.ReconcileState(after); }, "removed ownership cannot adopt newly added identical ACE");
            record.Phase = 0;
            record.Save(path); var loaded = Record.Load(path, policy);
            Check(loaded.Pid == 42 && loaded.Creation == 12345678 && loaded.Before.Digest == before.Digest && loaded.After.Digest == after.Digest, "write-ahead ownership round trip");
            record.Save(path); Check(Record.Load(path, policy).After.Digest == after.Digest, "atomic existing-record replacement");
            var changedPolicy = Policy(); changedPolicy.SqlImageSha256 = new string('b', 64);
            Refuses(delegate { Record.Load(path, changedPolicy); }, "ownership cannot replay across policy changes");
            record.After = before; record.Save(path);
            Refuses(delegate { Record.Load(path, policy); }, "forged proposal rejected by exact derivation");
            record.After = after; record.Save(path);
            using (var stream = new FileStream(path, FileMode.Append)) stream.WriteByte(0);
            Refuses(delegate { Record.Load(path, policy); }, "ownership trailing data rejected");
            return checks;
        }
    }
}
