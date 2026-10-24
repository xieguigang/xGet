Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports Nuget

''' <summary>
''' xConsole: the server terminal administration console of the xDoc nuget
''' server. the console opens the very same JSql database as the running
''' server process (the ``MultiProcessAccess`` storage option allows the
''' concurrent read) and provides:
''' 
''' + ``tables``    : browse the database tables (row pages);
''' + ``user``      : the account management (list / official / delete / reset);
''' + ``blacklist`` : the email account domain blacklist management;
''' + ``mail``      : configure, inspect and test the smtp account (the
'''                   configuration is stored salted encrypted in the database);
''' + ``db``        : force the write ahead log checkpoint;
''' + ``cluster``   : re-run the package umap + kmeans cluster analysis.
''' 
''' every command accepts the global ``--data &lt;dir&gt;`` option which points
''' to the data directory of the server (the same value as the server
''' ``--data`` argument). the default is ``./data``.
''' </summary>
Module Program

    Private Const UsageText As String =
        "xConsole - the xDoc nuget server administration console" & vbCrLf &
        vbCrLf &
        "usage: xConsole <command> [arguments] [--data <dir>]" & vbCrLf &
        vbCrLf &
        "commands:" & vbCrLf &
        "  tables                     list the database tables" & vbCrLf &
        "  tables <name>              print the rows of one table (--skip N --limit N)" & vbCrLf &
        "  user list                  list the registered accounts (email, official, demo, banned, created)" & vbCrLf &
        "  user official <email> <on|off>   set or clear the official badge of an account" & vbCrLf &
        "  user demo <email> <on|off>     set or clear the demo badge of an account" & vbCrLf &
        "  user banned <email> <on|off>   set or clear the upload ban of an account" & vbCrLf &
        "  user delete <email>        delete an account (its uploaded packages are kept)" & vbCrLf &
        "  user reset <email>         reset the TOTP secret and print the new otpauth link" & vbCrLf &
        "  blacklist list             list the blacklisted email account domains" & vbCrLf &
        "  blacklist add <domain>     add a domain to the registration blacklist" & vbCrLf &
        "  blacklist remove <domain>  remove a domain from the registration blacklist" & vbCrLf &
        "  mail set                   configure the smtp account (see the options below)" & vbCrLf &
        "  mail show                  print the current smtp configuration (password masked)" & vbCrLf &
        "  mail test --to <email>     send a test mail through the configured account" & vbCrLf &
        "  mail clear                 remove the stored smtp configuration" & vbCrLf &
        "  db checkpoint [--force]    merge the pending write ahead logs" & vbCrLf &
        "  cluster rebuild [--k N]    re-run the package umap + kmeans cluster analysis" & vbCrLf &
        "  package list               list the packages with their obsolete / hidden state" & vbCrLf &
        "  package list --anonymous   list the package ids without an uploader / owner" & vbCrLf &
        "  package list --owner <email>   list the packages associated with one account" & vbCrLf &
        "  package obsolete <id> <on|off>  mark or unmark a package as obsolete" & vbCrLf &
        "  package hide <id> <on|off>      hide or unhide a package from every public view" & vbCrLf &
        "  package owner list <id>         list the owners of one package id" & vbCrLf &
        "  package owner add <id> <email>     associate an account with a package as its owner" & vbCrLf &
        "  package owner remove <id> <email>  remove an account from the owners of a package" & vbCrLf &
        "  docs build <id> [version]   rebuild the static api document pages of a package" & vbCrLf &
        "  docs build --all            rebuild every package version which has documents" & vbCrLf &
        vbCrLf &
        "docs build options:" & vbCrLf &
        "  --reindex        re-extract the xml comments from the stored nupkg before the render" & vbCrLf &
        "  --wwwroot <dir>  the web root (default: the wwwroot folder next to --data)" & vbCrLf &
        "  --template <dir> the document template folder (default: the template next to --data)" & vbCrLf &
        vbCrLf &
        "mail set options:" & vbCrLf &
        "  --host <host>    the smtp server host (required)" & vbCrLf &
        "  --port <port>    the smtp server port (default 587)" & vbCrLf &
        "  --ssl <on|off>   use ssl/starttls (default on)" & vbCrLf &
        "  --user <user>    the smtp account name (optional)" & vbCrLf &
        "  --pass <pass>    the smtp account password (optional)" & vbCrLf &
        "  --from <mail>    the from address (required)" & vbCrLf &
        "  --sender <name>  the display name of the sender (optional)" & vbCrLf &
        vbCrLf &
        "global options:" & vbCrLf &
        "  --data, -d <dir>  the server data directory (default: ./data)" & vbCrLf &
        "  --yes, -y         answer yes to every confirmation prompt"

    ''' <summary>the shared database access of the console session.</summary>
    Private store As NugetStore

    ''' <summary>the resolved server configuration of the ``--data`` directory.</summary>
    Private config As NugetConfiguration

    ''' <summary>the ``--yes`` flag which skips every confirmation prompt.</summary>
    Private assumeYes As Boolean = False

    ''' <summary>show the untruncated cell values of the tables command (--full).</summary>
    Private fullValues As Boolean = False

    Function Main(args As String()) As Integer
        If args Is Nothing OrElse args.Length = 0 Then
            Call printUsage()
            Return 1
        End If

        Dim command As String = args(0).TrimStart("-"c, "/"c).ToLowerInvariant()
        Dim positional As New List(Of String)
        Dim options As Dictionary(Of String, String) = parseArgs(args, positional)

        If command = "help" OrElse command = "?" OrElse command = "h" Then
            Call printUsage()
            Return 0
        End If

        assumeYes = hasFlag(options, "yes", "y")

        ' resolve the data directory and open the database
        Dim dataDirectory As String = getOption(options, "data", "d")

        If String.IsNullOrEmpty(dataDirectory) Then
            dataDirectory = Path.Combine(App.CurrentDirectory, "data")

            If Not dataDirectory.DirectoryExists Then
                dataDirectory = Path.Combine(App.HOME, "..", "data").GetDirectoryFullPath
            End If
        End If

        config = NugetConfiguration.FromConfig(New Dictionary(Of String, String) From {
            {"data", dataDirectory}
        })

        If Not Directory.Exists(config.DataDirectory) Then
            Call Console.WriteLine($"the server data directory does not exist: {config.DataDirectory}")
            Call Console.WriteLine("pass the server data directory with the --data option.")
            Return 1
        End If

        store = New NugetStore(config.DatabaseDirectory, config.CreateStorageOptions())

        Select Case command
            Case "tables", "table", "t"
                Return tables(positional, options)
            Case "user", "users"
                Return user(positional, options)
            Case "blacklist", "black", "bl"
                Return blacklist(positional)
            Case "mail"
                Return mail(positional, options)
            Case "db", "database"
                Return db(positional, options)
            Case "cluster", "clusters"
                Return cluster(options)
            Case "package", "packages", "pkg"
                Return package(positional, options)
            Case "docs", "doc", "apidoc"
                Return docs(positional, options)
            Case Else
                Call Console.WriteLine($"unknown command: {args(0)}")
                Call printUsage()
                Return 1
        End Select
    End Function

#Region "tables"

    ''' <summary>
    ''' browse the database: without an argument the known table names are
    ''' listed; with a table name the rows are printed as a text table page.
    ''' </summary>
    Private Function tables(positional As List(Of String), options As Dictionary(Of String, String)) As Integer
        fullValues = options.ContainsKey("full")
        If positional.Count = 0 Then
            Call Console.WriteLine("the database tables:")
            Call Console.WriteLine()

            For Each name As String In NugetStore.TableNames
                Call Console.WriteLine($"  {name}")
            Next

            Call Console.WriteLine()
            Call Console.WriteLine("inspect one table with: xConsole tables <name> [--skip N --limit N]")
            Return 0
        End If

        Dim tableName As String = positional(0).ToLowerInvariant()
        Dim snapshot As TableSnapshot = store.ReadTable(tableName)

        If snapshot Is Nothing Then
            Call Console.WriteLine($"unknown table: '{tableName}' (run 'xConsole tables' for the table list)")
            Return 1
        End If

        Dim skip As Integer = intValue(options, "skip", 0)
        Dim limit As Integer = intValue(options, "limit", 20)

        Call Console.WriteLine($"table '{snapshot.name}': {snapshot.rows.Count} row(s), {snapshot.columns.Count} column(s)")
        Call Console.WriteLine()

        Dim columns As String() = snapshot.columns.ToArray()
        Dim rows As String()() = snapshot.rows _
            .Skip(skip) _
            .Take(If(limit <= 0, snapshot.rows.Count, limit)) _
            .ToArray()

        ' the column widths adapt to the longest cell of the printed page
        Dim widths(columns.Length - 1) As Integer

        For i As Integer = 0 To columns.Length - 1
            widths(i) = columns(i).Length
        Next

        For Each row As String() In rows
            For i As Integer = 0 To Math.Min(row.Length, columns.Length) - 1
                widths(i) = Math.Max(widths(i), Math.Min(row(i).Length, 48))
            Next
        Next

        Call printRow(columns, widths)
        Call printSeparator(widths)

        For Each row As String() In rows
            Call printRow(row, widths)
        Next

        Dim shown As Integer = rows.Length

        If skip + shown < snapshot.rows.Count Then
            Call Console.WriteLine()
            Call Console.WriteLine($"showing rows {skip + 1}-{skip + shown} of {snapshot.rows.Count} (--skip {skip + shown} for the next page)")
        End If

        Return 0
    End Function

    Private Sub printRow(cells As String(), widths As Integer())
        Dim parts As New List(Of String)

        For i As Integer = 0 To widths.Length - 1
            Dim text As String = If(i < cells.Length, cells(i), "")

            If text.Length > 48 AndAlso Not fullValues Then
                text = text.Substring(0, 45) & "..."
            End If

            Call parts.Add(text.PadRight(widths(i)))
        Next

        Call Console.WriteLine("  " & String.Join("  ", parts))
    End Sub

    Private Sub printSeparator(widths As Integer())
        Call Console.WriteLine("  " & String.Join("  ", widths.Select(Function(w) New String("-"c, w))))
    End Sub

#End Region

#Region "user management"

    ''' <summary>
    ''' the account management: list / official / delete / reset.
    ''' </summary>
    Private Function user(positional As List(Of String), options As Dictionary(Of String, String)) As Integer
        Dim action As String = If(positional.Count > 0, positional(0).ToLowerInvariant(), "list")

        Select Case action
            Case "list", "ls"
                Dim users As List(Of UserRecord) = store.ReadAllUsers()

                Call Console.WriteLine($"registered accounts: {users.Count}")
                Call Console.WriteLine()

                For Each item As UserRecord In users.OrderBy(Function(u) u.id)
                    Dim flags As UserFlagRecord = store.GetUserFlags(item.email)
                    Call Console.WriteLine($"  #{item.id}  {item.email}")
                    Call Console.WriteLine($"      official: {If(flags.official, "yes", "no")}   demo: {If(flags.demo, "yes", "no")}   banned: {If(flags.banned, "yes", "no")}")
                    Call Console.WriteLine($"      created: {item.created:yyyy-MM-dd HH:mm:ss} UTC")
                Next

                Return 0

            Case "official", "demo", "banned"
                Return setUserFlagAction(positional, action)

            Case "delete", "del", "rm"
                If positional.Count < 2 Then
                    Call Console.WriteLine("usage: xConsole user delete <email>")
                    Return 1
                End If

                Dim email As String = positional(1)

                If Not confirm($"delete the account '{email}'? (its uploaded packages are kept)") Then
                    Call Console.WriteLine("cancelled.")
                    Return 0
                End If

                If store.DeleteUser(email) Then
                    Call Console.WriteLine($"the account '{email}' was deleted.")
                    Return 0
                Else
                    Call Console.WriteLine($"no account was found for '{email}'.")
                    Return 1
                End If

            Case "reset"
                If positional.Count < 2 Then
                    Call Console.WriteLine("usage: xConsole user reset <email>")
                    Return 1
                End If

                Dim email As String = positional(1)

                If store.GetUser(email) Is Nothing Then
                    Call Console.WriteLine($"no account was found for '{email}'.")
                    Return 1
                End If

                If Not confirm($"reset the TOTP secret of '{email}'? the old secret becomes invalid.") Then
                    Call Console.WriteLine("cancelled.")
                    Return 0
                End If

                Dim salt As String = TotpAuth.GenerateSalt(TotpAuth.SaltLength)
                Dim secret As String = TotpModule.Base32Encode(TotpAuth.DeriveSecret(email, salt))

                Call store.UpdateUserSecret(email, salt, secret)

                Call Console.WriteLine($"the TOTP secret of '{email}' was reset.")
                Call Console.WriteLine()
                Call Console.WriteLine($"  new secret (base32): {secret}")
                Call Console.WriteLine($"  otpauth uri        : {TotpModule.BuildOtpAuthUri(secret, email, "nuget")}")
                Call Console.WriteLine()
                Call Console.WriteLine("the user has to replace the stored secret in the xGet account store")
                Call Console.WriteLine("(or to register the email again for the mail verification flow).")
                Return 0

            Case Else
                Call Console.WriteLine($"unknown user action: '{action}'")
                Call Console.WriteLine("available actions: list, official, demo, banned, delete, reset")
                Return 1
        End Select
    End Function

    ''' <summary>
    ''' the shared implementation of the ``user official`` / ``user demo`` /
    ''' ``user banned`` flag actions.
    ''' </summary>
    Private Function setUserFlagAction(positional As List(Of String), flagName As String) As Integer
        If positional.Count < 3 Then
            Call Console.WriteLine($"usage: xConsole user {flagName} <email> <on|off>")
            Return 1
        End If

        Dim email As String = positional(1)
        Dim flagText As String = positional(2).ToLowerInvariant()
        Dim flag As Boolean

        If flagText = "on" OrElse flagText = "true" OrElse flagText = "yes" Then
            flag = True
        ElseIf flagText = "off" OrElse flagText = "false" OrElse flagText = "no" Then
            flag = False
        Else
            Call Console.WriteLine($"invalid flag value: '{flagText}' (on or off is expected)")
            Return 1
        End If

        Call store.SetUserFlag(email, flagName, flag)

        Select Case flagName
            Case "official"
                Call Console.WriteLine($"the account '{email}' is {(If(flag, "now marked as", "no longer marked as"))} official.")
            Case "demo"
                Call Console.WriteLine($"the account '{email}' is {(If(flag, "now marked as", "no longer marked as"))} a demo account.")
            Case "banned"
                If flag Then
                    Call Console.WriteLine($"the account '{email}' is now banned (its upload requests are rejected).")
                Else
                    Call Console.WriteLine($"the ban of the account '{email}' was lifted.")
                End If
        End Select

        ' also report the account when it does not exist yet
        If store.GetUser(email) Is Nothing Then
            Call Console.WriteLine("note: this email has no registered account yet, the flag will apply after the registration.")
        End If

        Return 0
    End Function

    ''' <summary>
    ''' the ``package`` command group: browse the packages with their public
    ''' flags, and mark or unmark one package id as obsolete / hidden. the flags
    ''' are written straight into the ``package_flags`` table of the server
    ''' database, which the running server reads on every request.
    ''' </summary>
    Private Function package(positional As List(Of String), options As Dictionary(Of String, String)) As Integer
        Dim action As String = If(positional.Count > 0, positional(0).ToLowerInvariant(), "list")

        positional = New List(Of String)(positional.Skip(1))

        Select Case action
            Case "list", "ls"
                ' the anonymous filter enumerates the package ids which have no
                ' uploader / owner association recorded in the database.
                If hasFlag(options, "anonymous") Then
                    Return listAnonymousPackages()
                End If

                ' the owner filter enumerates the packages associated with one
                ' account (as uploader or manually assigned owner).
                Dim ownerEmail As String = getOption(options, "owner")
                If Not String.IsNullOrWhiteSpace(ownerEmail) Then
                    Return listPackagesByOwner(ownerEmail)
                End If

                Dim all As List(Of PackageRecord) = store.ReadAllPackages()
                Dim ids As List(Of String) = all _
                    .Where(Function(p) Not String.IsNullOrEmpty(p.package_id)) _
                    .Select(Function(p) p.package_id.ToLowerInvariant()) _
                    .Distinct(StringComparer.OrdinalIgnoreCase) _
                    .OrderBy(Function(id) id, StringComparer.OrdinalIgnoreCase) _
                    .ToList()

                Call Console.WriteLine($"packages: {ids.Count}")
                Call Console.WriteLine()

                For Each id As String In ids
                    Dim flags As PackageFlagRecord = store.GetPackageFlags(id)
                    Dim versions As Integer = all.Where(Function(p) String.Equals(p.package_id, id, StringComparison.OrdinalIgnoreCase)).Count()
                    Dim owner As String = store.GetPackageUploader(id)

                    Call Console.WriteLine($"  {id}")
                    Call Console.WriteLine($"      versions: {versions}   obsolete: {If(flags.obsolete, "yes", "no")}   hidden: {If(flags.hidden, "yes", "no")}")
                    Call Console.WriteLine($"      uploader: {If(String.IsNullOrEmpty(owner), "(not recorded)", owner)}")
                Next

                Return 0

            Case "obsolete", "deprecate"
                Return setPackageFlagAction(positional, "obsolete")

            Case "hide", "hidden", "unlist"
                Return setPackageFlagAction(positional, "hidden")

            Case "owner", "owners"
                Return packageOwnerAction(positional)

            Case Else
                Call Console.WriteLine($"unknown package action: '{action}'")
                Call Console.WriteLine("available actions: list, obsolete, hide, owner")
                Return 1
        End Select
    End Function

    ''' <summary>
    ''' the ``package list --anonymous`` action: print the package ids which have
    ''' no uploader nor owner association recorded in the database.
    ''' </summary>
    Private Function listAnonymousPackages() As Integer
        Dim ids As List(Of String) = store.GetAnonymousPackageIds()

        Call Console.WriteLine($"packages without an uploader / owner association: {ids.Count}")
        Call Console.WriteLine()

        If ids.Count = 0 Then
            Call Console.WriteLine("  (every package has at least one uploader or owner recorded)")
            Return 0
        End If

        For Each id As String In ids
            Call Console.WriteLine($"  {id}")
        Next

        Return 0
    End Function

    ''' <summary>
    ''' the ``package list --owner &lt;email&gt;`` action: print the package ids which
    ''' the given account is associated with (as uploader or manually assigned
    ''' owner).
    ''' </summary>
    Private Function listPackagesByOwner(email As String) As Integer
        Dim ids As List(Of String) = store.GetPackagesByOwner(email)

        Call Console.WriteLine($"packages associated with '{email}': {ids.Count}")
        Call Console.WriteLine()

        If ids.Count = 0 Then
            Call Console.WriteLine("  (no package is associated with this account)")
            Return 0
        End If

        For Each id As String In ids
            Call Console.WriteLine($"  {id}")
        Next

        Return 0
    End Function

    ''' <summary>
    ''' the ``package owner`` action group: manually associate one account with
    ''' one package id as its owner, or remove such an association again. the
    ''' owners of a package (its uploaders, its manually assigned owners and the
    ''' official administrator accounts) may publish new versions of it and
    ''' change its public flags through the web api.
    ''' </summary>
    ''' <remarks>
    ''' the ownership rows are written into the same database which the running
    ''' server reads, so a change applies to the web api right away.
    ''' </remarks>
    Private Function packageOwnerAction(positional As List(Of String)) As Integer
        Dim subAction As String = If(positional.Count > 0, positional(0).ToLowerInvariant(), "list")

        Select Case subAction
            Case "list", "ls"
                If positional.Count < 2 Then
                    Call Console.WriteLine("usage: xConsole package owner list <id>")
                    Return 1
                End If

                Dim packageId As String = positional(1).Trim()

                If Not store.PackageIdExists(packageId) Then
                    Call Console.WriteLine($"package not found: '{packageId}'")
                    Return 1
                End If

                Dim storedId As String = store.ResolvePackageId(packageId)
                Dim owners As List(Of String) = store.GetPackageOwners(storedId)
                Dim uploaders As List(Of String) = store.GetPackageUploaders(storedId)

                Call Console.WriteLine($"owners of '{storedId}':")
                Call Console.WriteLine()

                For Each mail As String In owners
                    Call Console.WriteLine($"  [owner]    {mail}")
                Next

                For Each mail As String In uploaders
                    If Not owners.Any(Function(e) e.Equals(mail, StringComparison.OrdinalIgnoreCase)) Then
                        Call Console.WriteLine($"  [uploader] {mail}")
                    End If
                Next

                If owners.Count = 0 AndAlso uploaders.Count = 0 Then
                    Call Console.WriteLine("  (no owner was recorded for this package)")
                End If

                Call Console.WriteLine()
                Call Console.WriteLine("every account above may publish new versions of this package")
                Call Console.WriteLine("and change its public flags through the web api.")
                Return 0

            Case "add", "grant"
                If positional.Count < 3 Then
                    Call Console.WriteLine("usage: xConsole package owner add <id> <email>")
                    Return 1
                End If

                Dim packageId As String = positional(1).Trim()
                Dim email As String = positional(2).Trim()

                If Not store.PackageIdExists(packageId) Then
                    Call Console.WriteLine($"package not found: '{packageId}'")
                    Return 1
                End If

                If Not confirm($"associate the account '{email}' with the package '{packageId}' as its owner?") Then
                    Call Console.WriteLine("cancelled.")
                    Return 0
                End If

                If store.AddPackageOwner(packageId, email) Then
                    Call Console.WriteLine($"the account '{email}' was associated with the package '{packageId}' as its owner.")

                    If store.GetUser(email) Is Nothing Then
                        Call Console.WriteLine("note: this email has no registered account yet, the ownership applies after the registration.")
                    End If

                    Return 0
                Else
                    Call Console.WriteLine($"the account '{email}' is already an owner of the package '{packageId}'.")
                    Return 1
                End If

            Case "remove", "revoke", "rm", "del"
                If positional.Count < 3 Then
                    Call Console.WriteLine("usage: xConsole package owner remove <id> <email>")
                    Return 1
                End If

                Dim packageId As String = positional(1).Trim()
                Dim email As String = positional(2).Trim()

                If Not store.PackageIdExists(packageId) Then
                    Call Console.WriteLine($"package not found: '{packageId}'")
                    Return 1
                End If

                If Not confirm($"remove the ownership of the account '{email}' on the package '{packageId}'?") Then
                    Call Console.WriteLine("cancelled.")
                    Return 0
                End If

                If store.RemovePackageOwner(packageId, email) Then
                    Call Console.WriteLine($"the account '{email}' is no longer an owner of the package '{packageId}'.")
                    Return 0
                Else
                    Call Console.WriteLine($"the account '{email}' is not a manually assigned owner of the package '{packageId}'.")
                    Call Console.WriteLine("(note: the uploader accounts of the package versions can not be removed manually.)")
                    Return 1
                End If

            Case Else
                Call Console.WriteLine($"unknown package owner action: '{subAction}'")
                Call Console.WriteLine("available actions: list, add, remove")
                Return 1
        End Select
    End Function

    ''' <summary>
    ''' the shared implementation of the ``package obsolete`` and ``package
    ''' hide`` flag actions.
    ''' </summary>
    Private Function setPackageFlagAction(positional As List(Of String), flagName As String) As Integer
        If positional.Count < 3 Then
            Call Console.WriteLine($"usage: xConsole package {flagName} <id> <on|off>")
            Return 1
        End If

        Dim packageId As String = positional(1).Trim()
        Dim flagText As String = positional(2).ToLowerInvariant()
        Dim flag As Boolean

        If flagText = "on" OrElse flagText = "true" OrElse flagText = "yes" Then
            flag = True
        ElseIf flagText = "off" OrElse flagText = "false" OrElse flagText = "no" Then
            flag = False
        Else
            Call Console.WriteLine($"invalid flag value: '{flagText}' (on or off is expected)")
            Return 1
        End If

        If Not store.PackageIdExists(packageId) Then
            Call Console.WriteLine($"package not found: '{packageId}'")
            Return 1
        End If

        Call store.SetPackageFlag(packageId, flagName, flag)

        Dim flags As PackageFlagRecord = store.GetPackageFlags(packageId)

        If flagName = "obsolete" Then
            Call Console.WriteLine($"the package '{packageId}' is {(If(flag, "now marked as", "no longer marked as"))} obsolete.")
        ElseIf flag Then
            Call Console.WriteLine($"the package '{packageId}' is now hidden: it is not listed, searchable, downloadable nor documented anymore.")
        Else
            Call Console.WriteLine($"the package '{packageId}' is visible again.")
        End If

        Call Console.WriteLine($"      obsolete: {If(flags.obsolete, "yes", "no")}   hidden: {If(flags.hidden, "yes", "no")}")

        If flagName = "hidden" AndAlso flag Then
            Call Console.WriteLine("note: a running server applies the flag to every request right away, but the package")
            Call Console.WriteLine("      files it has already mapped into its static file system need a restart to")
            Call Console.WriteLine("      disappear from the '/packages/...' urls.")
        End If

        Return 0
    End Function

#End Region

#Region "email domain blacklist"

    Private Function blacklist(positional As List(Of String)) As Integer
        Dim action As String = If(positional.Count > 0, positional(0).ToLowerInvariant(), "list")

        Select Case action
            Case "list", "ls"
                Dim domains As List(Of String) = store.GetBlacklistDomains()

                Call Console.WriteLine($"blacklisted email account domains: {domains.Count}")

                For Each domain As String In domains.OrderBy(Function(d) d)
                    Call Console.WriteLine($"  {domain}")
                Next

                Return 0

            Case "add"
                If positional.Count < 2 Then
                    Call Console.WriteLine("usage: xConsole blacklist add <domain>")
                    Return 1
                End If

                If store.AddBlacklistDomain(positional(1)) Then
                    Call Console.WriteLine($"the domain '{normalizeDomain(positional(1))}' was added to the registration blacklist.")
                    Return 0
                Else
                    Call Console.WriteLine("the domain is already blacklisted (or the value was empty).")
                    Return 1
                End If

            Case "remove", "rm", "del", "delete"
                If positional.Count < 2 Then
                    Call Console.WriteLine("usage: xConsole blacklist remove <domain>")
                    Return 1
                End If

                If store.RemoveBlacklistDomain(positional(1)) Then
                    Call Console.WriteLine($"the domain '{normalizeDomain(positional(1))}' was removed from the registration blacklist.")
                    Return 0
                Else
                    Call Console.WriteLine("the domain was not blacklisted.")
                    Return 1
                End If

            Case Else
                Call Console.WriteLine($"unknown blacklist action: '{action}'")
                Call Console.WriteLine("available actions: list, add, remove")
                Return 1
        End Select
    End Function

    Private Function normalizeDomain(domain As String) As String
        Return If(domain, "").Trim().TrimStart("@"c).ToLowerInvariant()
    End Function

#End Region

#Region "mail configuration"

    ''' <summary>
    ''' the smtp account management: the configuration is stored salted
    ''' encrypted (aes-gcm + pbkdf2 + the machine local ``mail.key`` key file)
    ''' inside the ``server_settings`` table.
    ''' </summary>
    Private Function mail(positional As List(Of String), options As Dictionary(Of String, String)) As Integer
        Dim action As String = If(positional.Count > 0, positional(0).ToLowerInvariant(), "show")

        Select Case action
            Case "set", "config"
                Dim cfg As New MailConfig With {
                    .host = getOption(options, "host"),
                    .port = intValue(options, "port", 587),
                    .user = getOption(options, "user", "username"),
                    .password = getOption(options, "pass", "password"),
                    .from = getOption(options, "from"),
                    .senderName = getOption(options, "sender", "sender-name")
                }

                Dim sslText As String = getOption(options, "ssl")

                If sslText.StringEmpty() Then
                    cfg.ssl = Not hasFlag(options, "no-ssl")
                Else
                    cfg.ssl = Not {"off", "false", "0", "no"}.Contains(sslText.ToLowerInvariant())
                End If

                If Not cfg.IsValid() Then
                    Call Console.WriteLine("the smtp configuration is incomplete: --host and --from are required.")
                    Call Console.WriteLine("example: xConsole mail set --host smtp.example.com --port 587 --from noreply@example.com --user x --pass y")
                    Return 1
                End If

                Call MailService.SaveConfig(store, config.DataDirectory, cfg)
                Call Console.WriteLine($"the smtp configuration was saved (salted encrypted) into {config.DatabaseDirectory}.")
                Call printMailConfig(cfg)
                Return 0

            Case "show", "print"
                Dim cfg As MailConfig = MailService.LoadConfig(store, config.DataDirectory)

                If cfg Is Nothing Then
                    Call Console.WriteLine("no smtp configuration is stored.")
                    Call Console.WriteLine("configure the mail server with: xConsole mail set --host <host> --from <mail> ...")
                    Return 1
                End If

                Call printMailConfig(cfg)
                Return 0

            Case "test"
                Dim cfg As MailConfig = MailService.LoadConfig(store, config.DataDirectory)

                If cfg Is Nothing OrElse Not cfg.IsValid() Then
                    Call Console.WriteLine("no usable smtp configuration is stored: run 'xConsole mail set' first.")
                    Return 1
                End If

                Dim [to] As String = getOption(options, "to")

                If [to].StringEmpty() Then
                    Call Console.WriteLine("usage: xConsole mail test --to <email>")
                    Return 1
                End If

                Dim body As String =
                    "<html><body style=""font-family:'Segoe UI',sans-serif;"">" &
                    "<h2 style=""color:#062E9A;"">xConsole test mail</h2>" &
                    "<p>this is a test message sent by the xConsole mail test command.</p>" &
                    $"<p>server time: {Date.UtcNow:yyyy-MM-dd HH:mm:ss} UTC</p></body></html>"
                Dim error_ As String = ""

                If MailService.Send(cfg, [to], "xConsole mail test", body, error_) Then
                    Call Console.WriteLine($"the test mail was sent to '{[to]}'.")
                    Return 0
                Else
                    Call Console.WriteLine($"the test mail failed: {error_}")
                    Return 2
                End If

            Case "clear", "remove", "reset"
                If Not confirm("remove the stored smtp configuration?") Then
                    Call Console.WriteLine("cancelled.")
                    Return 0
                End If

                Call MailService.ClearConfig(store)
                Call Console.WriteLine("the smtp configuration was removed.")
                Return 0

            Case Else
                Call Console.WriteLine($"unknown mail action: '{action}'")
                Call Console.WriteLine("available actions: set, show, test, clear")
                Return 1
        End Select
    End Function

    Private Sub printMailConfig(cfg As MailConfig)
        Call Console.WriteLine()
        Call Console.WriteLine($"  host       : {cfg.host}")
        Call Console.WriteLine($"  port       : {cfg.port}")
        Call Console.WriteLine($"  ssl        : {If(cfg.ssl, "on", "off")}")
        Call Console.WriteLine($"  user       : {cfg.user}")

        If String.IsNullOrEmpty(cfg.password) Then
            Call Console.WriteLine("  password   : (not set)")
        Else
            Call Console.WriteLine("  password   : ********")
        End If

        Call Console.WriteLine($"  from       : {cfg.from}")
        Call Console.WriteLine($"  sender name: {cfg.senderName}")
        Call Console.WriteLine()
    End Sub

#End Region

#Region "database checkpoint"

    ''' <summary>
    ''' merge the pending write ahead logs of the database into the data files.
    ''' the forced mode merges even when the engine does not consider a table
    ''' idle.
    ''' </summary>
    Private Function db(positional As List(Of String), options As Dictionary(Of String, String)) As Integer
        Dim action As String = If(positional.Count > 0, positional(0).ToLowerInvariant(), "checkpoint")

        If action <> "checkpoint" AndAlso action <> "merge" Then
            Call Console.WriteLine($"unknown db action: '{action}' (checkpoint is expected)")
            Return 1
        End If

        Dim force As Boolean = hasFlag(options, "force", "f")
        Dim watch As Stopwatch = Stopwatch.StartNew()
        Dim merged As Integer = store.Checkpoint(force)

        watch.Stop()
        Call Console.WriteLine($"database checkpoint {(If(force, "(forced) ", ""))}finished: {merged} table(s) merged in {watch.ElapsedMilliseconds}ms.")
        Return 0
    End Function

#End Region

#Region "cluster analysis"

    ''' <summary>
    ''' re-run the package tag matrix -> umap -> kmeans cluster analysis.
    ''' </summary>
    Private Function cluster(options As Dictionary(Of String, String)) As Integer
        Dim k As Integer = intValue(options, "k", 0)

        If k <> 0 AndAlso (k < 2 OrElse k > 64) Then
            Call Console.WriteLine("invalid --k value: an integer between 2 and 64 is expected")
            Return 1
        End If

        Call Console.WriteLine($"running the package cluster analysis (umap + kmeans{(If(k > 0, $", k={k}", ""))}) ...")

        Dim watch As Stopwatch = Stopwatch.StartNew()
        Dim summary As PackageClusterAnalysis.AnalysisSummary = PackageClusterAnalysis.RunIfChanged(store, config, forceK:=k)

        watch.Stop()

        If summary.Success Then
            Call Console.WriteLine($"cluster analysis finished in {watch.ElapsedMilliseconds}ms:")
            Call Console.WriteLine($"  {summary.Message}")
            Call Console.WriteLine($"  samples={summary.Samples}, tags={summary.Tags}, k={summary.K}, clusters={summary.Clusters}")
            Return 0
        Else
            Call Console.WriteLine($"the cluster analysis failed: {summary.Message}")
            Return 2
        End If
    End Function

#End Region

#Region "api document cache"

    ''' <summary>
    ''' the outcome of one static page build of a package version.
    ''' </summary>
    Private Enum DocBuildResult
        ''' <summary>the pages were rendered from the stored document rows.</summary>
        Built
        ''' <summary>the version has no document row, its stale pages were removed.</summary>
        Removed
        ''' <summary>the build failed, the previous pages are left untouched.</summary>
        Failed
    End Enum

    ''' <summary>
    ''' one package version of the document cache, with the type full names which
    ''' the database holds for it.
    ''' </summary>
    Private Class DocTarget
        Public Property package_id As String
        Public Property version As String
        Public Property types As New List(Of String)
    End Class

    ''' <summary>
    ''' rebuild the static api document pages of one package version, of one
    ''' package (every one of its versions) or of every package which has
    ''' documents.
    ''' 
    ''' the ``ApiDocWorker`` daemon can not be addressed from here: it consumes
    ''' the stdin of the server process which started it, so this console runs the
    ''' very same build itself through <see cref="DocPageCache"/>. The database is
    ''' opened in the multi process mode of this console session, so a running
    ''' server is not disturbed.
    ''' </summary>
    ''' <param name="positional">``build``, the package id and an optional version.</param>
    ''' <param name="options">``--all``, ``--reindex``, ``--wwwroot``, ``--template``.</param>
    Private Function docs(positional As List(Of String), options As Dictionary(Of String, String)) As Integer
        Dim action As String = If(positional.Count > 0, positional(0).ToLowerInvariant(), "build")

        If action <> "build" AndAlso action <> "rebuild" Then
            Call Console.WriteLine($"unknown docs action: '{action}' (build is expected)")
            Call printDocsUsage()
            Return 1
        End If

        Dim all As Boolean = hasFlag(options, "all", "a")
        Dim reindex As Boolean = hasFlag(options, "reindex", "re")
        Dim packageId As String = If(positional.Count > 1, positional(1).Trim(), "")
        Dim version As String = If(positional.Count > 2, positional(2).Trim(), "")

        If Not all AndAlso String.IsNullOrEmpty(packageId) Then
            Call printDocsUsage()
            Return 1
        End If

        Dim docConfig As NugetConfiguration = docsConfiguration(options)
        Dim targets As List(Of DocTarget) = docTargets(all, packageId, version)

        Call Console.WriteLine($"document cache : {docConfig.DocsStaticDirectory}")
        Call Console.WriteLine($"template       : {docConfig.TemplateDirectory}")

        If targets.Count = 0 Then
            Call Console.WriteLine("no api document was found for the requested package (check the id / version)")
            Return 2
        End If

        Dim reindexText As String = If(reindex, ", re-extracting the xml comments", "")

        Call Console.WriteLine($"targets        : {targets.Count} package version(s){reindexText}")
        Call Console.WriteLine()

        Dim watch As Stopwatch = Stopwatch.StartNew()
        Dim written As Integer = 0
        Dim removed As Integer = 0
        Dim failed As Integer = 0

        For Each target As DocTarget In targets
            Dim pages As Integer = 0
            Dim outcome As DocBuildResult = buildDocTarget(docConfig, target, reindex, pages)

            Select Case outcome
                Case DocBuildResult.Built
                    written += 1
                    Call Console.WriteLine($"  {target.package_id} {target.version}: {target.types.Count} type(s) -> {pages} page(s)")
                Case DocBuildResult.Removed
                    removed += 1
                    Call Console.WriteLine($"  {target.package_id} {target.version}: no document row, the stale pages were removed")
                Case Else
                    failed += 1
                    Call Console.WriteLine($"  {target.package_id} {target.version}: FAILED")
            End Select
        Next

        Dim globalIndex As Boolean = rebuildGlobalDocIndex(docConfig)
        watch.Stop()

        Call Console.WriteLine()
        Call Console.WriteLine($"document cache rebuilt in {watch.ElapsedMilliseconds}ms: " &
                              $"{written} written, {removed} removed, {failed} failed, global index={globalIndex}")

        Return If(failed = 0, 0, 3)
    End Function

    ''' <summary>
    ''' render one package version and write its pages; with ``reindex`` the xml
    ''' comment documents are extracted from the stored nupkg again and the
    ''' database rows are replaced first (the same step the daemon performs after
    ''' an upload).
    ''' </summary>
    ''' <param name="docConfig"></param>
    ''' <param name="target"></param>
    ''' <param name="reindex"></param>
    ''' <param name="pages">the number of the written pages.</param>
    Private Function buildDocTarget(docConfig As NugetConfiguration, target As DocTarget,
                                    reindex As Boolean, ByRef pages As Integer) As DocBuildResult

        Dim rows As List(Of PackageApiDocRecord)

        If reindex Then
            Dim nupkg As String = nupkgFile(target.package_id, target.version)

            If Not File.Exists(nupkg) Then
                Call Console.WriteLine($"    the package file was not found: {nupkg}")
                Return DocBuildResult.Failed
            End If

            Dim warnings As New List(Of String)
            Dim document As Readership.ApiDocDocument = PackageApiDocs.ExtractInProcess(nupkg, warnings)

            For Each message As String In warnings
                Call Console.WriteLine($"    warning: {message}")
            Next

            If document Is Nothing Then
                Call Console.WriteLine("    the xml comment documents could not be extracted")
                Return DocBuildResult.Failed
            End If

            rows = PackageApiDocs.ToRecords(document, target.package_id, target.version)
            Call store.ReplacePackageApiDocs(target.package_id, target.version, rows)
        Else
            rows = store.ReadPackageApiDocs(target.package_id, target.version)
        End If

        If rows.Count = 0 Then
            Call DocPageCache.RemoveVersion(docConfig, target.package_id, target.version)
            Return DocBuildResult.Removed
        End If

        Dim versions As List(Of String) = store.GetPackageApiDocVersions(target.package_id)

        pages = DocPageCache.WritePackageVersion(docConfig, target.package_id, target.version, rows, versions)

        Return DocBuildResult.Built
    End Function

    ''' <summary>
    ''' rebuild the cross package ``docs/index.html`` from the stored type index
    ''' rows.
    ''' </summary>
    Private Function rebuildGlobalDocIndex(docConfig As NugetConfiguration) As Boolean
        Dim written As Boolean = DocPageCache.WriteGlobalIndex(docConfig, store.ReadApiDocIndex())

        Call Console.WriteLine($"  global index: {DocPageCache.GlobalIndexFilePath(docConfig)} (written={written})")

        Return written
    End Function

    ''' <summary>
    ''' collect the package versions which have api documents, optionally limited
    ''' to one package id and one version. one single index read is enough.
    ''' </summary>
    Private Function docTargets(all As Boolean, packageId As String, version As String) As List(Of DocTarget)
        Dim groups As New Dictionary(Of String, DocTarget)(StringComparer.OrdinalIgnoreCase)

        For Each row As PackageApiDocRecord In store.ReadApiDocIndex()
            If row Is Nothing OrElse String.IsNullOrEmpty(row.package_id) OrElse String.IsNullOrEmpty(row.version) Then
                Continue For
            End If

            If Not all Then
                If Not row.package_id.Equals(packageId, StringComparison.OrdinalIgnoreCase) Then
                    Continue For
                End If

                If Not String.IsNullOrEmpty(version) AndAlso
                   Not row.version.Equals(version, StringComparison.OrdinalIgnoreCase) Then
                    Continue For
                End If
            End If

            Dim key As String = row.package_id.ToLowerInvariant() & "|" & row.version.ToLowerInvariant()
            Dim target As DocTarget = Nothing

            If Not groups.TryGetValue(key, target) Then
                target = New DocTarget With {
                    .package_id = row.package_id,
                    .version = row.version
                }

                groups(key) = target
            End If

            If Not String.IsNullOrEmpty(row.type_fullname) Then
                Call target.types.Add(row.type_fullname)
            End If
        Next

        Return groups.Values _
            .OrderBy(Function(t) t.package_id, StringComparer.OrdinalIgnoreCase) _
            .ThenBy(Function(t) t.version, StringComparer.OrdinalIgnoreCase) _
            .ToList()
    End Function

    ''' <summary>
    ''' the document configuration of the console: the web root and the template
    ''' folder default to the folders next to the ``--data`` directory, exactly
    ''' like the deployment layout of the server.
    ''' </summary>
    Private Function docsConfiguration(options As Dictionary(Of String, String)) As NugetConfiguration
        Dim parent As String = Path.GetDirectoryName(Path.GetFullPath(config.DataDirectory))
        Dim wwwroot As String = getOption(options, "wwwroot", "w")
        Dim template As String = getOption(options, "template")

        If String.IsNullOrEmpty(wwwroot) Then
            wwwroot = Path.Combine(parent, "wwwroot")
        End If

        If String.IsNullOrEmpty(template) Then
            template = Path.Combine(parent, "template")
        End If

        Return NugetConfiguration.FromConfig(New Dictionary(Of String, String) From {
            {"db", config.DatabaseDirectory},
            {"packages", config.PackageDirectory},
            {"wwwroot", wwwroot},
            {"template", template},
            {"sitemap-enabled", "false"},
            {"cluster-enabled", "false"},
            {"apidoc-worker-enabled", "false"}
        })
    End Function

    ''' <summary>
    ''' the physical nupkg path of one package version, using the flat container
    ''' layout of the server (``{packages}/{id}/{version}/{id}.{version}.nupkg``).
    ''' </summary>
    Private Function nupkgFile(packageId As String, version As String) As String
        Dim id As String = If(packageId, "").ToLowerInvariant()
        Dim ver As String = If(version, "").ToLowerInvariant()

        Return Path.Combine(config.PackageDirectory, id, ver, $"{id}.{ver}.nupkg")
    End Function

    Private Sub printDocsUsage()
        Call Console.WriteLine("usage: xConsole docs build <package-id> [version] [--reindex] [--wwwroot <dir>] [--template <dir>]")
        Call Console.WriteLine("       xConsole docs build --all [--reindex] [--wwwroot <dir>] [--template <dir>]")
    End Sub

#End Region

#Region "command line helpers"

    ''' <summary>
    ''' parse the command line: every ``--name value`` (or ``--name=value``)
    ''' token is stored in the options table, every other token becomes a
    ''' positional argument.
    ''' </summary>
    Private Function parseArgs(args As String(), positional As List(Of String)) As Dictionary(Of String, String)
        Dim options As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        Dim i As Integer = 1

        While i < args.Length
            Dim token As String = args(i)

            If token.StartsWith("-"c) OrElse token.StartsWith("/"c) Then
                Dim name As String = token.TrimStart("-"c, "/"c)
                Dim equals As Integer = name.IndexOf("="c)

                If equals >= 0 Then
                    options(name.Substring(0, equals)) = name.Substring(equals + 1)
                    i += 1
                Else
                    Dim value As String = ""
                    If i + 1 < args.Length AndAlso Not args(i + 1).StartsWith("-"c) Then
                        value = args(i + 1)
                        i += 2
                    Else
                        i += 1
                    End If
                    options(name) = value
                End If
            Else
                Call positional.Add(token)
                i += 1
            End If
        End While

        Return options
    End Function

    Private Function hasFlag(options As Dictionary(Of String, String), ParamArray names As String()) As Boolean
        For Each name As String In names
            If options.ContainsKey(name) Then
                Return True
            End If
        Next
        Return False
    End Function

    Private Function getOption(options As Dictionary(Of String, String), ParamArray names As String()) As String
        For Each name As String In names
            Dim value As String = Nothing
            If options.TryGetValue(name, value) AndAlso Not String.IsNullOrEmpty(value) Then
                Return value.Trim()
            End If
        Next
        Return ""
    End Function

    Private Function intValue(options As Dictionary(Of String, String), name As String, fallback As Integer) As Integer
        Dim raw As String = getOption(options, name)
        Dim value As Integer

        If String.IsNullOrEmpty(raw) OrElse Not Integer.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, value) Then
            Return fallback
        End If

        Return value
    End Function

    ''' <summary>
    ''' ask the operator for a confirmation; the ``--yes`` global option skips
    ''' every prompt.
    ''' </summary>
    Private Function confirm(message As String) As Boolean
        If assumeYes Then
            Return True
        End If

        Call Console.Write($"{message} [y/N]: ")
        Dim answer As String = Console.ReadLine()
        Return answer IsNot Nothing AndAlso {"y", "yes"}.Contains(answer.Trim().ToLowerInvariant())
    End Function

    Private Sub printUsage()
        Call Console.WriteLine(UsageText)
    End Sub

#End Region
End Module
