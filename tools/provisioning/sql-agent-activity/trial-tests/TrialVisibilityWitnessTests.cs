using System.Text.RegularExpressions;

namespace SqlObserver.AgentActivityTrial.Tests;

public sealed class TrialVisibilityWitnessTests
{
    [Fact]
    public void ExactBracketedForeignOwnerFixtureProducesOnlySafeSummary()
    {
        var (before,after,capture)=Fixture();
        Assert.Equal(new TrialVisibilityResult(2,1,true,true,true),TrialVisibilityWitness.ValidateComparison(before,after,capture));
        Assert.All(typeof(TrialVisibilityResult).GetProperties(),p=>Assert.True(p.PropertyType==typeof(int) || p.PropertyType==typeof(bool)));
    }

    [Theory]
    [InlineData("identity")] [InlineData("connection")] [InlineData("sql-session")] [InlineData("agent-session")]
    [InlineData("agent-start")] [InlineData("owner")] [InlineData("rows")] [InlineData("utc-offset")]
    [InlineData("reversed")] [InlineData("long-command")] [InlineData("empty")] [InlineData("no-foreign")]
    [InlineData("duplicate")] [InlineData("unsorted")] [InlineData("missing-source")] [InlineData("overflow")]
    public void ChangedOrIncompleteDbaEvidenceCannotQualify(string mutation)
    {
        var (before,after,capture)=Fixture();
        after=mutation switch {
            "identity"=>after with {Identity=after.Identity with {MasterDatabaseGuid=Guid.NewGuid()}},
            "connection"=>after with {ConnectionId=Guid.NewGuid()},
            "sql-session"=>after with {SessionId=after.SessionId+1},
            "agent-session"=>after with {AgentSessionId=after.AgentSessionId+1},
            "agent-start"=>after with {AgentStartedLocal=after.AgentStartedLocal.AddMilliseconds(3)},
            "owner"=>after with {Rows=[after.Rows[0] with {ForeignOwner=true},after.Rows[1]]},
            "rows"=>after with {Rows=[after.Rows[0],after.Rows[1] with {Source=after.Rows[1].Source with {LastExecutedStepId=5}}]},
            "utc-offset"=>after with {StartedAtUtc=after.StartedAtUtc.ToOffset(TimeSpan.FromHours(1))},
            "reversed"=>after with {CompletedAtUtc=after.StartedAtUtc.AddTicks(-1)},
            "long-command"=>after with {CompletedAtUtc=after.StartedAtUtc.AddSeconds(11)},
            "empty"=>after with {Rows=[]},
            "no-foreign"=>after with {Rows=after.Rows.Select(x=>x with {ForeignOwner=false}).ToArray()},
            "duplicate"=>after with {Rows=[after.Rows[1],after.Rows[1]]},
            "unsorted"=>after with {Rows=after.Rows.Reverse().ToArray()},
            "missing-source"=>after with {Rows=[new(null!,true)]},
            _=>after with {Rows=Enumerable.Repeat(after.Rows[1],513).ToArray()}
        };
        Assert.Throws<InvalidDataException>(()=>TrialVisibilityWitness.ValidateComparison(before,after,capture));
    }

    [Theory]
    [InlineData("before-bracket")] [InlineData("after-bracket")] [InlineData("reversed")] [InlineData("long")]
    [InlineData("offset")] [InlineData("before-session")] [InlineData("after-session")] [InlineData("before-local")]
    [InlineData("after-local")] [InlineData("local-kind")] [InlineData("truncated")] [InlineData("source-count")]
    [InlineData("missing-job")] [InlineData("duplicate")] [InlineData("order")] [InlineData("foreign-missing")]
    public void CaptureBoundaryAndAccountingMismatchesCannotQualify(string mutation)
    {
        var (before,after,capture)=Fixture();
        capture=mutation switch {
            "before-bracket"=>capture with {ObservedFromUtc=before.CompletedAtUtc.AddTicks(-1)},
            "after-bracket"=>capture with {ObservedToUtc=after.StartedAtUtc.AddTicks(1)},
            "reversed"=>capture with {ObservedToUtc=capture.ObservedFromUtc.AddTicks(-1)},
            "long"=>capture with {ObservedToUtc=capture.ObservedFromUtc.AddSeconds(11)},
            "offset"=>capture with {ObservedFromUtc=capture.ObservedFromUtc.ToOffset(TimeSpan.FromHours(-1))},
            "before-session"=>capture with {BeforeAgentSessionId=null},
            "after-session"=>capture with {AfterAgentSessionId=999},
            "before-local"=>capture with {BeforeAgentStartedLocal=null},
            "after-local"=>capture with {AfterAgentStartedLocal=capture.AfterAgentStartedLocal!.Value.AddMilliseconds(3)},
            "local-kind"=>capture with {BeforeAgentStartedLocal=DateTime.SpecifyKind(capture.BeforeAgentStartedLocal!.Value,DateTimeKind.Utc)},
            "truncated"=>capture with {Truncated=true},
            "source-count"=>capture with {SourceRowsRead=3},
            "missing-job"=>capture with {Jobs=[capture.Jobs[0]]},
            "duplicate"=>capture with {Jobs=[capture.Jobs[0],capture.Jobs[0]]},
            "order"=>capture with {Jobs=capture.Jobs.Reverse().ToArray()},
            _=>capture with {Jobs=[capture.Jobs[0],capture.Jobs[1] with {JobId=Guid.Parse("00000003-0000-0000-0000-000000000000")} ]}
        };
        Assert.Throws<InvalidDataException>(()=>TrialVisibilityWitness.ValidateComparison(before,after,capture));
    }

    [Theory]
    [InlineData("id")] [InlineData("session")] [InlineData("requested")] [InlineData("queued")]
    [InlineData("started")] [InlineData("stopped")] [InlineData("step")] [InlineData("step-time")]
    public void EverySourceTupleFieldIsComparedExactly(string field)
    {
        var (before,after,capture)=Fixture();
        var row=capture.Jobs[1];
        DateTime changed=new(2026,10,5,1,2,3,DateTimeKind.Unspecified);
        row=field switch {
            "id"=>row with {JobId=Guid.Parse("00000003-0000-0000-0000-000000000000")},
            "session"=>row with {AgentSessionId=9},
            "requested"=>row with {RequestedLocal=changed}, "queued"=>row with {QueuedLocal=changed},
            "started"=>row with {StartExecutionLocal=changed}, "stopped"=>row with {StopExecutionLocal=changed},
            "step"=>row with {LastExecutedStepId=1}, _=>row with {LastExecutedStepLocal=changed}
        };
        Assert.Throws<InvalidDataException>(()=>TrialVisibilityWitness.ValidateComparison(before,after,capture with {Jobs=[capture.Jobs[0],row]}));
    }

    [Fact]
    public void LocalDatesRemainOffsetFreeAndAreNotOrderedAsElapsedTime()
    {
        var (before,after,capture)=Fixture();
        DateTime later=new(2026,10,5,4,0,0,DateTimeKind.Unspecified);
        var reversedLocal=before.Rows[1].Source with {StartExecutionLocal=later,StopExecutionLocal=later.AddHours(-1)};
        var rows=new[]{before.Rows[0],before.Rows[1] with {Source=reversedLocal}};
        Assert.True(TrialVisibilityWitness.ValidateComparison(before with {Rows=rows},after with {Rows=rows},capture with {Jobs=rows.Select(x=>x.Source).ToArray()}).ExactMatch);
        reversedLocal=reversedLocal with {StartExecutionLocal=DateTime.SpecifyKind(later,DateTimeKind.Utc)};
        Assert.Throws<InvalidDataException>(()=>TrialVisibilityWitness.ValidateComparison(before,after,capture with {Jobs=[capture.Jobs[0],reversedLocal]}));
    }

    [Fact]
    public void ExactTimeBoundaryEqualityIsAllowedWithoutTolerance()
    {
        var (before,after,capture)=Fixture();
        Assert.True(TrialVisibilityWitness.ValidateComparison(before,after,capture with {
            ObservedFromUtc=before.CompletedAtUtc,ObservedToUtc=after.StartedAtUtc
        }).BracketContained);
    }

    [Fact]
    public void QueryUsesBoundedUnfilteredOwnerWitnessAndNoMutation()
    {
        string query=TrialVisibilityWitness.Query;
        Assert.Contains("SELECT TOP(513)",query,StringComparison.Ordinal);
        Assert.Contains("LEFT JOIN msdb.dbo.sysjobs AS j ON j.job_id=a.job_id",query,StringComparison.Ordinal);
        Assert.Contains("j.owner_sid IS NULL OR DATALENGTH(j.owner_sid)=0 THEN NULL",query,StringComparison.Ordinal);
        Assert.Contains("WHEN j.owner_sid=@monitor_sid THEN 0 ELSE 1",query,StringComparison.Ordinal);
        Assert.Contains("WHERE a.session_id=@before_session",query,StringComparison.Ordinal);
        Assert.Contains("ORDER BY CONVERT(char(36),a.job_id) COLLATE Latin1_General_100_BIN2",query,StringComparison.Ordinal);
        Assert.Equal(2,Regex.Count(query,"FROM msdb.dbo.syssessions ORDER BY session_id DESC",RegexOptions.CultureInvariant));
        Assert.DoesNotMatch(@"(?i)\b(CREATE|ALTER|DROP|GRANT|REVOKE|DENY|INSERT|UPDATE|DELETE|EXEC|EXECUTE)\b",query);
        Assert.DoesNotContain("j.name",query,StringComparison.Ordinal);
    }

    private static (TrialVisibilitySnapshot Before,TrialVisibilitySnapshot After,TrialVisibilityCapture Capture) Fixture()
    {
        var identity=TrialFixture.Create().Baseline.Snapshot.Identity;
        var connection=Guid.Parse("44444444-4444-4444-4444-444444444444");
        var utc=new DateTimeOffset(2026,10,5,8,0,0,TimeSpan.Zero);
        DateTime local=new(2026,10,4,6,0,0,DateTimeKind.Unspecified);
        TrialVisibilityRow[] rows=[
            new(new(Guid.Parse("00000001-0000-0000-0000-000000000000"),7,null,null,null,null,null,null),false),
            new(new(Guid.Parse("00000002-0000-0000-0000-000000000000"),7,null,null,null,null,null,null),true)
        ];
        var before=new TrialVisibilitySnapshot(identity,connection,55,utc,utc.AddSeconds(1),7,local,rows);
        var after=before with {StartedAtUtc=utc.AddSeconds(4),CompletedAtUtc=utc.AddSeconds(5)};
        var capture=new TrialVisibilityCapture(utc.AddSeconds(2),utc.AddSeconds(3),7,local,7,local,rows.Select(x=>x.Source).ToArray(),2,false);
        return(before,after,capture);
    }
}
