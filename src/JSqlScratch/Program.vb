Imports JSql.Engine
Imports JSql.Storage
Imports Nuget

Module Program

    ''' <summary>
    ''' mimic the xConsole usage: separate process runs, MultiProcessAccess on,
    ''' same SQL as NugetStore.AddBlacklistDomain / RemoveBlacklistDomain.
    ''' mode arg: 1 = insert, 2 = read, 3 = delete, 4 = read again
    ''' </summary>
    ''' <summary>test mode 9: validate a nupkg with the PackageValidator (args(2) = nupkg path).</summary>
    Function ValidateTest(args As String()) As Integer
        ' debug: parse the pe header of every lib dll by hand
        Using zip As IO.Compression.ZipArchive = IO.Compression.ZipFile.OpenRead(args(2))
            For Each entry In zip.Entries
                Dim name = entry.FullName.Replace("\"c, "/"c)

                If name.StartsWith("lib/") AndAlso name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) Then
                    Using s = entry.Open()
                        Dim buffer(4095) As Byte
                        Dim total As Integer = 0
                        Dim n As Integer
                        Do While total < buffer.Length
                            n = s.Read(buffer, total, buffer.Length - total)
                            If n <= 0 Then Exit Do
                            total += n
                        Loop
                        Console.WriteLine($"entry={name} total={total}")
                        Console.WriteLine($"  first16: {BitConverter.ToString(buffer, 0, 16)}")
                        Dim pe = buffer(&H3C) Or (buffer(&H3D) << 8) Or (buffer(&H3E) << 16) Or (buffer(&H3F) << 24)
                        Console.WriteLine($"  pe={pe}")
                        Console.WriteLine($"  managed={PackageValidator.IsManagedAssembly(s)}")
                    End Using
                End If
            Next
        End Using

        Dim reason As String = ""
        Dim ok As Boolean = PackageValidator.Validate(args(2), reason)
        Call Console.WriteLine($"validate={ok}  reason={reason}")

        ' args(3) = source dll: rebuild a clean test package with the .net
        ' zip writer (the powershell compress-archive output behaved odd) and
        ' validate it again
        If args.Length > 3 Then
            Dim made As String = args(2) & ".made.nupkg"

            Using fs As New IO.FileStream(made, IO.FileMode.Create)
                Using zip As New IO.Compression.ZipArchive(fs, IO.Compression.ZipArchiveMode.Create)
                    Dim entry As IO.Compression.ZipArchiveEntry = zip.CreateEntry("lib/net10.0/Demo.dll")

                    Using target As IO.Stream = entry.Open()
                        Using source As New IO.FileStream(args(3), IO.FileMode.Open, IO.FileAccess.Read)
                            Call source.CopyTo(target)
                        End Using
                    End Using
                End Using
            End Using

            reason = ""
            ok = PackageValidator.Validate(made, reason)
            Call Console.WriteLine($"made package: validate={ok}  reason={reason}")

            ' control: the raw dll on disk
            Using disk As New IO.FileStream(args(3), IO.FileMode.Open, IO.FileAccess.Read)
                Call Console.WriteLine($"disk dll managed={PackageValidator.IsManagedAssembly(disk)}")
            End Using
        End If

        Return 0
    End Function

    Function Main(args As String()) As Integer
        Dim dir As String = args(0)
        Dim mode As Integer = CInt(args(1))
        Dim mp As Boolean = args.Length < 3 OrElse args(2) <> "single"
        Dim engine As New SqlEngine(dir, New StorageOptions With {
            .MergeIdleSeconds = 30,
            .MergeAfterOperations = 2000,
            .FsyncEachWrite = False,
            .MultiProcessAccess = mp,
            .MergeOnStatementEnd = args.Length >= 4 AndAlso args(3) = "nomerge"
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
            Case 9
                Return ValidateTest(args)
            Case 1
                Call engine.Execute("INSERT INTO email_blacklist (id, domain, created) VALUES (1, 'spam.example.com', '2026-10-06 00:00:00')")
                Call Console.WriteLine("inserted")
            Case 2
                Dim rs As ResultSet = engine.Execute("SELECT domain FROM email_blacklist")
                Call Console.WriteLine($"rows: {rs.Rows.Count}")
            Case 3
                Dim del As ResultSet = engine.Execute("DELETE FROM email_blacklist WHERE domain = 'spam.example.com'")
                Call Console.WriteLine($"delete says: {del.Message}")

                Dim same As ResultSet = engine.Execute("SELECT domain FROM email_blacklist")
                Call Console.WriteLine($"same-process rows: {same.Rows.Count}")

                Dim merged As Integer = engine.MergeAll(force:=True)
                Call Console.WriteLine($"MergeAll merged: {merged}")
        End Select

        Return 0
    End Function
End Module
