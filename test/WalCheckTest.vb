Imports System.IO
Imports JSql.Engine
Imports JSql.Storage

''' <summary>
''' A diagnostic of the JSql write ahead log checkpoint: it writes into a
''' temporary database, subscribes to the checkpoint diagnostics of the engine
''' and then waits while the engine is idle, so that the "does the background
''' checkpoint merge the WAL back into the data file" question could be answered
''' without the http server.
''' 
''' usage: test walcheck [idleSeconds] [waitSeconds]
''' </summary>
Module WalCheckTest

    Private ReadOnly messageLog As New List(Of String)
    Private ReadOnly logLock As New Object

    Public Function Run(args As String()) As Integer
        Dim idleSeconds As Integer = argInt(args, 0, 5)
        Dim waitSeconds As Integer = argInt(args, 1, 20)
        Dim root As String = Path.Combine(Path.GetTempPath(), "xdoc-walcheck-" & Guid.NewGuid().ToString("N"))

        Dim options As New StorageOptions With {
            .MergeIdleSeconds = idleSeconds,
            .MergeAfterOperations = 100000,
            .Verbose = True
        }
        Dim engine As New SqlEngine(root, options)

        Call Console.WriteLine($"idle threshold : {idleSeconds}s")
        Call Console.WriteLine($"idle wait      : {waitSeconds}s")
        Call Console.WriteLine($"engine options : MergeIdleSeconds={engine.Storage.MergeIdleSeconds} MergeAfterOperations={engine.Storage.MergeAfterOperations}")
        Call Console.WriteLine($"database root  : {root}")
        Call Console.WriteLine()


        AddHandler engine.CheckpointScheduler.Info, AddressOf onCheckpointInfo
        AddHandler engine.Sessions.Info, AddressOf onSessionInfo

        Try
            Call Console.WriteLine($"scheduler idle : {engine.CheckpointScheduler.IdleSeconds:0.0}s (busy={engine.CheckpointScheduler.BusyCount})")
            Call Console.WriteLine($"scheduler      : running={engine.CheckpointScheduler.IsRunning} ticks={engine.CheckpointScheduler.TickCount}")

            Call engine.Execute("CREATE DATABASE IF NOT EXISTS xdoc")
            Call engine.Execute("USE xdoc")
            Call engine.Execute("CREATE TABLE IF NOT EXISTS t (id INT NOT NULL PRIMARY KEY, name VARCHAR(50))")

            For i As Integer = 1 To 5
                Call engine.Execute($"INSERT INTO t (id, name) VALUES ({i}, 'row-{i}')")
            Next

            Dim dir As String = engine.DataStore.DatabaseDir("xdoc")

            Call report(engine, dir, "after the inserts")

            For elapsed As Integer = 1 To waitSeconds
                Threading.Thread.Sleep(1000)
                Call Console.WriteLine($"  idle={engine.CheckpointScheduler.IdleSeconds:0.0}s busy={engine.CheckpointScheduler.BusyCount} " &
                                       $"ticks={engine.CheckpointScheduler.TickCount} " &
                                       $"lastMerge={engine.CheckpointScheduler.LastMergeTime:HH:mm:ss} " &
                                       $"wal={fileSize(Path.Combine(dir, "t.jsonl.wal"))} data={fileSize(Path.Combine(dir, "t.jsonl"))}")
            Next

            Dim mergedByScheduler As Boolean = fileSize(Path.Combine(dir, "t.jsonl")) > 0
            Call report(engine, dir, "after the idle window")

            Call Console.WriteLine($"scheduler state: {engine.CheckpointScheduler}")

            If mergedByScheduler Then
                Call Console.WriteLine("result         : the background checkpoint merged the WAL")
            Else
                Call Console.WriteLine("result         : the background checkpoint did NOT merge the WAL")

                Dim forced As Integer = engine.MergeAll(force:=True)

                Call Console.WriteLine($"forced merge   : {forced} table(s)")
                Call report(engine, dir, "after the forced CHECKPOINT")
            End If

            If engine.CheckpointScheduler.LastError IsNot Nothing Then
                Call Console.WriteLine($"last error     : {engine.CheckpointScheduler.LastError}")
            End If

            Call Console.WriteLine($"scheduler state: {engine.CheckpointScheduler}")

            Call flushMessages()

            Return If(mergedByScheduler, 0, 1)
        Finally
            RemoveHandler engine.CheckpointScheduler.Info, AddressOf onCheckpointInfo
            RemoveHandler engine.Sessions.Info, AddressOf onSessionInfo

            Try
                engine.Dispose()
            Catch
            End Try

            Try
                If Directory.Exists(root) Then
                    Call Directory.Delete(root, recursive:=True)
                End If
            Catch
            End Try
        End Try
    End Function

    Private Sub report(engine As SqlEngine, dir As String, title As String)
        Dim session As ITableSession = engine.Sessions.TryGet(dir, "t")

        Call Console.WriteLine($"-- {title} --")

        If session Is Nothing Then
            Call Console.WriteLine("  session: not open")
        Else
            Call Console.WriteLine($"  pending={session.PendingOperations} hasPending={session.HasPendingChanges} " &
                                   $"buffered={session.PendingBufferedLines} lines={session.LineCount} " &
                                   $"wal={session.WalFileSize} data={session.DataFileSize}")
        End If

        Call Console.WriteLine($"  file   : data={fileSize(Path.Combine(dir, "t.jsonl"))} wal={fileSize(Path.Combine(dir, "t.jsonl.wal"))}")
    End Sub

    Private Function fileSize(path As String) As Long
        If File.Exists(path) Then
            Return New FileInfo(path).Length
        End If

        Return 0
    End Function

    Private Sub onCheckpointInfo(message As String)
        Call addMessage("checkpoint: " & message)
    End Sub

    Private Sub onSessionInfo(message As String)
        Call addMessage("sessions: " & message)
    End Sub

    Private Sub addMessage(text As String)
        SyncLock logLock
            Call messageLog.Add($"  [{Date.Now:HH:mm:ss}] {text}")
        End SyncLock
    End Sub

    Private Sub flushMessages()
        SyncLock logLock
            If messageLog.Count = 0 Then
                Call Console.WriteLine("diagnostics    : (no checkpoint message was raised)")
            Else
                Call Console.WriteLine("diagnostics    :")

                For Each line As String In messageLog
                    Call Console.WriteLine(line)
                Next
            End If
        End SyncLock
    End Sub

    Private Function argInt(args As String(), index As Integer, fallback As Integer) As Integer
        If args IsNot Nothing AndAlso args.Length > index Then
            Dim value As Integer

            If Integer.TryParse(args(index), value) AndAlso value > 0 Then
                Return value
            End If
        End If

        Return fallback
    End Function
End Module
