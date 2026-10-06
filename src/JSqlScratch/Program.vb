Imports JSql.Engine
Imports JSql.Storage

Module Program

    ''' <summary>
    ''' mimic the xConsole usage: separate process runs, MultiProcessAccess on,
    ''' same SQL as NugetStore.AddBlacklistDomain / RemoveBlacklistDomain.
    ''' mode arg: 1 = insert, 2 = read, 3 = delete, 4 = read again
    ''' </summary>
    Function Main(args As String()) As Integer
        Dim dir As String = args(0)
        Dim mode As Integer = CInt(args(1))
        Dim engine As New SqlEngine(dir, New StorageOptions With {
            .MergeIdleSeconds = 30,
            .MergeAfterOperations = 2000,
            .FsyncEachWrite = False,
            .MultiProcessAccess = True
        })

        Call engine.Execute("CREATE DATABASE IF NOT EXISTS nuget")
        Call engine.Execute("USE nuget")
        Call engine.Execute(
            "CREATE TABLE IF NOT EXISTS email_blacklist (" &
            "  id INT NOT NULL PRIMARY KEY," &
            "  domain VARCHAR(255) NOT NULL," &
            "  created DATETIME" &
            ")")

        Select Case mode
            Case 1
                Call engine.Execute("INSERT INTO email_blacklist (id, domain, created) VALUES (1, 'spam.example.com', '2026-10-06 00:00:00')")
                Call Console.WriteLine("inserted")
            Case 2
                Dim rs As ResultSet = engine.Execute("SELECT domain FROM email_blacklist")
                Call Console.WriteLine($"rows: {rs.Rows.Count}")
            Case 3
                Call engine.Execute("DELETE FROM email_blacklist WHERE domain = 'spam.example.com'")
                Call engine.MergeAll(force:=True)
                Call Console.WriteLine("deleted + merged")
        End Select

        Return 0
    End Function
End Module
