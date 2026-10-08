Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports Flute.Http.Core
Imports Flute.Http.Core.HttpStream
Imports Flute.Http.Core.Message
Imports Flute.Http.Core.Message.HttpHeader
Imports Microsoft.VisualBasic.Net.Http
Imports Microsoft.VisualBasic.Net.Protocols.ContentTypes

''' <summary>
''' the experimental nuget server controller.
''' </summary>
''' <remarks>
''' this class is loaded through reflection by the Fluteway ``/run`` command.
''' it exposes the nuget v3 protocol endpoints (service index, flat container,
''' registration and search), the experimental TOTP protected upload endpoints
''' and the json endpoints consumed by the static web front end.
''' </remarks>
Public Class Service
    Implements IHttpAppModule

    Private config As NugetConfiguration
    Private store As NugetStore
    Private auth As TotpAuth
    Private router As HttpRouter

    ''' <summary>
    ''' the sliding window rate limiter of the unauthenticated ``/api/register``
    ''' and ``/api/reset`` endpoints. it is created during ``Mount`` with the
    ''' configured window width and limits.
    ''' </summary>
    Private rateLimiter As RateLimiter

    ''' <summary>
    ''' the timer driving the periodic package cluster analysis. the instance is
    ''' kept in a field because an unreferenced timer would be garbage
    ''' collected and the periodic task would silently stop.
    ''' </summary>
    Private analysisTimer As System.Threading.Timer

    ''' <summary>
    ''' the timer driving the periodic database checkpoint. the instance is kept
    ''' in a field because an unreferenced timer would be garbage collected and
    ''' the periodic task would silently stop.
    ''' </summary>
    Private checkpointTimer As System.Threading.Timer

    ''' <summary>
    ''' the schedule which regenerates the site map when the api document
    ''' database changed and the server is idle. it is kept in a field because
    ''' the response helpers report the request activity to it.
    ''' </summary>
    Private sitemap As SitemapScheduler

    ''' <summary>
    ''' the cache life time of the static resources of the ``wwwroot`` folder:
    ''' the scripts, the style sheets, the images and the pages of the web
    ''' front end do not change between two releases, so the browser is told
    ''' to reuse its local copy for a month instead of asking the server again.
    ''' </summary>
    Private Const StaticCacheSeconds As Integer = 30 * 24 * 60 * 60

    Public Sub Mount(router As HttpRouter, config As IReadOnlyDictionary(Of String, String)) Implements IHttpAppModule.Mount
        Me.router = router
        Me.config = NugetConfiguration.FromConfig(config)

        Call Directory.CreateDirectory(Me.config.DataDirectory)
        Call Directory.CreateDirectory(Me.config.PackageDirectory)
        Call Directory.CreateDirectory(Me.config.DatabaseDirectory)
        Call Directory.CreateDirectory(Me.config.TempDirectory)

        Me.store = New NugetStore(Me.config.DatabaseDirectory, Me.config.CreateStorageOptions())
        Me.auth = New TotpAuth(Me.store)
        Me.rateLimiter = New RateLimiter(Me.config.RateWindowSeconds)

        ' the host attaches the physical package folder to the ``/packages/`` urls
        ' before this module is mounted, so the packages which are already hidden
        ' are withdrawn from the static file system here as well
        Call applyHiddenStaticFiles()

        Call $"nuget server data directory: {Me.config.DataDirectory}".info()

        Call registerStyleSheetMime()
        Call enableStaticFileCache()

        If Me.config.ClusterEnabled Then
            ' the first run is delayed so that it does not compete with the
            ' server startup, then it repeats on the configured interval.
            Me.analysisTimer = New System.Threading.Timer(
                AddressOf analysisTick,
                Nothing,
                dueTime:=TimeSpan.FromSeconds(30),
                period:=TimeSpan.FromMinutes(Me.config.ClusterIntervalMinutes))

            Call $"package cluster analysis scheduled: every {Me.config.ClusterIntervalMinutes} minute(s), k={Me.config.ClusterK}".info()
        Else
            Call "package cluster analysis is disabled by the configuration".info()
        End If

        ' a low frequency explicit checkpoint: it merges the write ahead logs of
        ' the database even when the engine never stays idle long enough for its
        ' own background checkpoint, so the wal files can not grow forever.
        Me.checkpointTimer = New System.Threading.Timer(
            AddressOf checkpointTick,
            Nothing,
            dueTime:=TimeSpan.FromSeconds(Me.config.DbCheckpointSeconds),
            period:=TimeSpan.FromSeconds(Me.config.DbCheckpointSeconds))

        Call $"database checkpoint scheduled: every {Me.config.DbCheckpointSeconds} second(s), idle merge after {Me.config.DbMergeIdleSeconds}s".info()

        ' the site map schedule: it rebuilds the sitemap.xml inside the scratch
        ' directory when the api document database changed and no controller
        ' request was served for a while. it is assigned to the field before it
        ' is started so that a request which arrives during the startup could
        ' already report its activity.
        Me.sitemap = New SitemapScheduler(Me.config, Me.store)
        Call Me.sitemap.Start()
    End Sub

    ''' <summary>
    ''' teach the static file system the ``.xsl`` extension so that the sitemap
    ''' style sheet is served as xml; without it the browser would refuse to
    ''' apply a style sheet delivered as ``application/octet-stream``. the mime
    ''' table is a process wide cache, the patch is therefore idempotent, and a
    ''' failure is only a warning because the site map itself still works.
    ''' </summary>
    Private Shared Sub registerStyleSheetMime()
        Try
            Dim table As Dictionary(Of String, ContentType) = TryCast(MIME.SuffixTable, Dictionary(Of String, ContentType))

            If table Is Nothing Then
                Call "the mime table is read only, the .xsl style sheet may be served as a binary stream".warning()
                Return
            End If

            table(".xsl") = New ContentType("XSLT stylesheet", "application/xml", ".xsl")
        Catch ex As Exception
            Call $"the .xsl mime type could not be registered: {ex.Message}".warning()
        End Try
    End Sub

    ''' <summary>
    ''' tell the browser that the static resources of the ``wwwroot`` folder
    ''' (the scripts, the style sheets, the images, the style sheet of the
    ''' site map and the html pages of the web front end) may be served from
    ''' its own cache for a month. no extension filter is applied, so every
    ''' static file of the site is covered.
    ''' </summary>
    ''' <remarks>
    ''' the static files are served by the file system listener of the host
    ''' before the clr routes are consulted, so the policy has to be applied
    ''' on the listener itself; it is mounted before this module.
    ''' </remarks>
    Private Sub enableStaticFileCache()
        If router Is Nothing OrElse router.FileSystem Is Nothing Then
            Call "the static file cache is not enabled: no file system listener was mounted".warning()
            Return
        End If

        router.FileSystem.CacheMaxAge = StaticCacheSeconds

        Call $"static file cache enabled: {StaticCacheSeconds \ 86400} day(s) for the wwwroot resources".info()
    End Sub

    ''' <summary>
    ''' the periodic database checkpoint: merge the pending write ahead logs. every
    ''' exception is swallowed (and logged) so that the background task can never
    ''' take the server down.
    ''' </summary>
    Private Sub checkpointTick(state As Object)
        Try
            Dim merged As Integer = store.Checkpoint()

            If merged > 0 Then
                Call $"database checkpoint: merged {merged} table(s)".debug()
            End If
        Catch ex As Exception
            Call App.LogException(ex)
        End Try
    End Sub

    ''' <summary>
    ''' the periodic analysis callback: rebuild the tag matrix / umap / kmeans
    ''' result when the feed has changed. every exception is swallowed (and
    ''' logged) so that the background task can never take the server down.
    ''' </summary>
    Private Sub analysisTick(state As Object)
        Try
            Dim summary As PackageClusterAnalysis.AnalysisSummary = PackageClusterAnalysis.RunIfChanged(store, config)

            If summary.Rebuilt Then
                Call $"package cluster analysis: {summary.Message} (elapsed={summary.ElapsedMilliseconds}ms)".info()
            ElseIf summary.Success Then
                Call $"package cluster analysis skipped: {summary.Message}".debug()
            Else
                Call $"package cluster analysis failed: {summary.Message}".warning()
            End If
        Catch ex As Exception
            Call App.LogException(ex)
        End Try
    End Sub

#Region "service index"

    <HttpGet("/v3/index.json")>
    Public Sub ServiceIndex(req As HttpRequest, res As HttpResponse)
        Dim baseUrl As String = getBaseUrl(req)

        Dim resources As New List(Of Object) From {
            resource($"{baseUrl}/v3-flatcontainer/", "PackageBaseAddress/3.0.0", "Package base address (flat container)"),
            resource($"{baseUrl}/v3/registration/", "RegistrationsBaseUrl/3.6.0", "Package registration metadata"),
            resource($"{baseUrl}/v3/registration/", "RegistrationsBaseUrl/3.4.0", "Package registration metadata"),
            resource($"{baseUrl}/v3/search", "SearchQueryService/3.0.0-beta", "Package search"),
            resource($"{baseUrl}/v3/autocomplete", "SearchAutocompleteService/3.0.0-beta", "Package autocomplete"),
            resource($"{baseUrl}/api/v2/package", "PackagePublish/2.0.0", "Package publish endpoint")
        }

        res.AccessControlAllowOrigin = "*"
        writeJson(res, New Dictionary(Of String, Object) From {
            {"version", "3.0.0"},
            {"resources", resources}
        })
    End Sub

#End Region

#Region "flat container"

    <HttpGet("/v3-flatcontainer/{id}/index.json")>
    Public Sub FlatContainerVersions(req As HttpRequest, res As HttpResponse)
        Dim id As String = routeValue(req, "id")
        Dim versions As List(Of PackageRecord) = store.GetVersions(id)

        If versions.Count = 0 Then
            res.WriteError(HTTP_RFC.RFC_NOT_FOUND, $"package '{id}' was not found")
            Return
        End If

        Dim data As String() = versions _
            .Select(Function(v) v.version.ToLowerInvariant()) _
            .ToArray()

        res.AccessControlAllowOrigin = "*"
        writeJson(res, New Dictionary(Of String, Object) From {
            {"versions", data}
        })
    End Sub

    <HttpGet("/v3-flatcontainer/{id}/{version}/{file}")>
    Public Sub FlatContainerDownload(req As HttpRequest, res As HttpResponse)
        Dim id As String = routeValue(req, "id")
        Dim version As String = routeValue(req, "version")
        Dim fileName As String = routeValue(req, "file")
        Dim pkg As PackageRecord = store.GetPackage(id, version)

        If pkg Is Nothing Then
            res.WriteError(HTTP_RFC.RFC_NOT_FOUND, $"package '{id}' {version} was not found")
            Return
        End If

        If fileName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) Then
            Dim nuspec As String = nuspecFilePath(pkg)

            If File.Exists(nuspec) Then
                res.AccessControlAllowOrigin = "*"
                res.SendFile(nuspec)
            Else
                res.WriteError(HTTP_RFC.RFC_NOT_FOUND, "the nuspec manifest was not found")
            End If
        Else
            Dim package As String = nupkgFilePath(pkg)

            If File.Exists(package) Then
                ' count both the lifetime version counter and the daily activity
                Call store.RecordDownload(pkg.package_id, pkg.version)
                res.AccessControlAllowOrigin = "*"
                res.SendFile(package)
            Else
                res.WriteError(HTTP_RFC.RFC_NOT_FOUND, "the package file was not found")
            End If
        End If
    End Sub

#End Region

#Region "registration"

    <HttpGet("/v3/registration/{id}/index.json")>
    Public Sub RegistrationIndex(req As HttpRequest, res As HttpResponse)
        Dim id As String = routeValue(req, "id")
        Dim versions As List(Of PackageRecord) = store.GetVersions(id)

        If versions.Count = 0 Then
            res.WriteError(HTTP_RFC.RFC_NOT_FOUND, $"package '{id}' was not found")
            Return
        End If

        Dim baseUrl As String = getBaseUrl(req)
        Dim idLower As String = versions.First().package_id.ToLowerInvariant()
        Dim leaves As New List(Of Object)

        For Each pkg As PackageRecord In versions
            leaves.Add(registrationLeaf(baseUrl, pkg))
        Next

        Dim range As New Dictionary(Of String, Object) From {
            {"@id", $"{baseUrl}/v3/registration/{idLower}/index.json"},
            {"count", leaves.Count},
            {"lower", versions.First().version.ToLowerInvariant()},
            {"upper", versions.Last().version.ToLowerInvariant()},
            {"items", leaves}
        }

        res.AccessControlAllowOrigin = "*"
        writeJson(res, New Dictionary(Of String, Object) From {
            {"count", 1},
            {"items", New List(Of Object) From {range}}
        })
    End Sub

    <HttpGet("/v3/registration/{id}/{version}.json")>
    Public Sub RegistrationLeaf(req As HttpRequest, res As HttpResponse)
        Dim id As String = routeValue(req, "id")
        Dim version As String = routeValue(req, "version")
        Dim pkg As PackageRecord = store.GetPackage(id, version)

        If pkg Is Nothing Then
            res.WriteError(HTTP_RFC.RFC_NOT_FOUND, $"package '{id}' {version} was not found")
            Return
        End If

        res.AccessControlAllowOrigin = "*"
        writeJson(res, registrationLeaf(getBaseUrl(req), pkg))
    End Sub

    Private Function registrationLeaf(baseUrl As String, pkg As PackageRecord) As Dictionary(Of String, Object)
        Dim idLower As String = pkg.package_id.ToLowerInvariant()
        Dim versionLower As String = pkg.version.ToLowerInvariant()
        Dim content As String = $"{baseUrl}/v3-flatcontainer/{idLower}/{versionLower}/{idLower}.{versionLower}.nupkg"

        Return New Dictionary(Of String, Object) From {
            {"@id", $"{baseUrl}/v3/registration/{idLower}/{versionLower}.json"},
            {"catalogEntry", catalogEntry(baseUrl, pkg)},
            {"listed", pkg.listed},
            {"packageContent", content},
            {"published", isoDate(pkg.published)},
            {"registration", $"{baseUrl}/v3/registration/{idLower}/index.json"}
        }
    End Function

    Private Function catalogEntry(baseUrl As String, pkg As PackageRecord) As Dictionary(Of String, Object)
        Dim idLower As String = pkg.package_id.ToLowerInvariant()
        Dim versionLower As String = pkg.version.ToLowerInvariant()
        Dim content As String = $"{baseUrl}/v3-flatcontainer/{idLower}/{versionLower}/{idLower}.{versionLower}.nupkg"

        Return New Dictionary(Of String, Object) From {
            {"@id", $"{baseUrl}/v3/catalog/{idLower}/{versionLower}.json"},
            {"id", pkg.package_id},
            {"version", pkg.version},
            {"description", pkg.description},
            {"authors", pkg.authors},
            {"iconUrl", ""},
            {"language", ""},
            {"licenseUrl", ""},
            {"licenseExpression", If(pkg.license, "")},
            {"listed", pkg.listed},
            {"minClientVersion", ""},
            {"packageContent", content},
            {"projectUrl", If(pkg.project_url, "")},
            {"published", isoDate(pkg.published)},
            {"requireLicenseAcceptance", False},
            {"summary", If(pkg.description, "")},
            {"tags", splitTags(pkg.tags)},
            {"title", pkg.package_id},
            {"versionDownloadCount", pkg.downloads},
            {"downloadCount", pkg.downloads},
            {"dependencyGroups", dependencyGroups(pkg)}
        }
    End Function

    Private Function dependencyGroups(pkg As PackageRecord) As List(Of Object)
        Dim dependencies As List(Of DependencyInfo) = parseDependencies(pkg.dependencies)

        If dependencies.Count = 0 Then
            Return New List(Of Object)
        End If

        Dim items As New List(Of Object)
        For Each dependency As DependencyInfo In dependencies
            items.Add(New Dictionary(Of String, Object) From {
                {"id", dependency.id},
                {"range", dependency.range}
            })
        Next

        Return New List(Of Object) From {
            New Dictionary(Of String, Object) From {
                {"targetFramework", ""},
                {"dependencies", items}
            }
        }
    End Function

#End Region

#Region "search"

    <HttpGet("/v3/search")>
    Public Sub Search(req As HttpRequest, res As HttpResponse)
        Dim keyword As String = queryValue(req, "q")
        Dim skip As Integer = queryInt(req, "skip", 0)
        Dim take As Integer = queryInt(req, "take", 20)
        Dim baseUrl As String = getBaseUrl(req)

        Dim groups As List(Of PackageSearchResult) = store.GroupPackages(keyword)
        Dim page As List(Of PackageSearchResult) = groups.Skip(skip).Take(take).ToList()
        Dim data As New List(Of Object)

        For Each group As PackageSearchResult In page
            Dim idLower As String = group.package_id.ToLowerInvariant()
            Dim versions As New List(Of Object)

            For Each version As PackageRecord In group.versions
                versions.Add(New Dictionary(Of String, Object) From {
                    {"version", version.version},
                    {"downloads", version.downloads},
                    {"@id", $"{baseUrl}/v3/registration/{idLower}/{version.version.ToLowerInvariant()}.json"}
                })
            Next

            data.Add(New Dictionary(Of String, Object) From {
                {"@id", $"{baseUrl}/v3/registration/{idLower}/index.json"},
                {"id", idLower},
                {"version", group.latest.version},
                {"description", group.latest.description},
                {"versions", versions},
                {"authors", splitTags(group.latest.authors)},
                {"tags", splitTags(group.latest.tags)},
                {"totalDownloads", group.total_downloads},
                {"verified", False},
                {"packageTypes", New List(Of Object) From {
                    New Dictionary(Of String, Object) From {{"name", "Dependency"}}
                }}
            })
        Next

        res.AccessControlAllowOrigin = "*"
        writeJson(res, New Dictionary(Of String, Object) From {
            {"totalHits", groups.Count},
            {"data", data}
        })
    End Sub

    <HttpGet("/v3/autocomplete")>
    Public Sub Autocomplete(req As HttpRequest, res As HttpResponse)
        Dim packageId As String = queryValue(req, "id")

        If Not String.IsNullOrEmpty(packageId) Then
            Dim versions As List(Of PackageRecord) = store.GetVersions(packageId)
            res.AccessControlAllowOrigin = "*"
            writeJson(res, New Dictionary(Of String, Object) From {
                {"totalHits", versions.Count},
                {"data", versions.Select(Function(v) v.version).ToArray()}
            })
            Return
        End If

        Dim keyword As String = queryValue(req, "q")
        Dim groups As List(Of PackageSearchResult) = store.GroupPackages(keyword)

        res.AccessControlAllowOrigin = "*"
        writeJson(res, New Dictionary(Of String, Object) From {
            {"totalHits", groups.Count},
            {"data", groups.Take(20).Select(Function(g) g.package_id.ToLowerInvariant()).ToArray()}
        })
    End Sub

#End Region

#Region "register and upload"

    <HttpPost("/api/register")>
    Public Sub RegisterUser(req As HttpPOSTRequest, res As HttpResponse)
        ' ---- registration switch ----
        If Not config.RegistrationEnabled Then
            Call "registration rejected: the registration is disabled".warning()
            writeResult(res, False, "the user registration is disabled on this server.", New Dictionary(Of String, Object) From {
                {"warning", "registration-disabled"}
            })
            Return
        End If

        Dim email As String = argument(req, "email")

        If String.IsNullOrEmpty(email) Then
            res.WriteError(HTTP_RFC.RFC_BAD_REQUEST, "the email argument is required")
            Return
        End If

        ' ---- rate limit: one mailbox and one client ip can start only a few
        ' registration requests inside the configured sliding window. the check
        ' runs before every further test and its response does not reveal
        ' whether the email address is registered on this server.
        If Not rateLimiter.TryAcquire(
            New RateLimiter.RateLimit(RateLimiter.EmailKey(email), config.RegisterEmailLimit),
            New RateLimiter.RateLimit(RateLimiter.IpKey(req.Remote), config.RegisterIpLimit)) Then

            Call $"registration rejected: rate limit exceeded for '{email}'".warning()
            res.WriteError(HTTP_RFC.RFC_TOO_MANY_REQUEST,
                           "too many registration requests from this address: please wait a few minutes and try again.")
            Return
        End If

        ' ---- email account domain blacklist ----
        If store.IsEmailBlacklisted(email) Then
            Call $"registration rejected: the domain of '{email}' is blacklisted".warning()
            writeResult(res, False, "the domain of this email address is blacklisted on this server and can not be registered.",
                        New Dictionary(Of String, Object) From {
                {"warning", "domain-blacklisted"}
            })
            Return
        End If

        ' ---- a usable smtp configuration is required for the verification flow ----
        If Not MailService.IsConfigured(store, config.DataDirectory) Then
            Call "registration rejected: the mail server is not configured".warning()
            writeResult(res, False, "the mail server is not configured on this server, so the verification email can not be sent. " &
                                    "please remind the server administrator to configure the mail server (xConsole mail set).",
                        New Dictionary(Of String, Object) From {
                {"warning", "mail-not-configured"}
            })
            Return
        End If

        ' an already verified account is never registered twice: the TOTP secret
        ' is never returned over the wire anymore. a lost authorization code is
        ' recovered through the self service secret reset flow.
        If store.GetUser(email) IsNot Nothing Then
            writeResult(res, True, "this email address is already registered on this server. " &
                                   "if you have lost your local authorization code, run " &
                                   "'xGet reset --server <server-url> --email <email>' to receive a fresh one by mail.",
                        New Dictionary(Of String, Object) From {
                {"warning", "already-registered"}
            })
            Return
        End If

        ' ---- create the pending registration and send the verification mail ----
        Call store.DeleteExpiredRegistrations()

        Dim salt As String = TotpAuth.GenerateSalt(TotpAuth.SaltLength)
        Dim secret As String = TotpModule.Base32Encode(TotpAuth.DeriveSecret(email, salt))
        Dim token As String = generateToken()
        Dim expires As Date = Date.UtcNow.AddMinutes(config.VerifyTtlMinutes)

        Call store.CreatePendingRegistration(email, token, salt, secret, expires)

        Dim serverUrl As String = getBaseUrl(req)
        Dim verifyUrl As String = $"{serverUrl}/api/verify?token={Uri.EscapeDataString(token)}"
        Dim body As String = MailService.RenderVerifyEmail(config.TemplateDirectory, email, verifyUrl, serverUrl, config.VerifyTtlMinutes)
        Dim mailError As String = ""

        If Not MailService.Send(MailService.LoadConfig(store, config.DataDirectory), email,
                                $"verify your email address on {serverUrl}", body, mailError) Then
            ' the pending record would never be received by the user, so it is
            ' removed right away instead of blocking the email for 30 minutes
            Dim orphan As PendingRegistrationRecord = store.GetPendingRegistration(token)

            If orphan IsNot Nothing Then
                Call store.DeletePendingRegistration(orphan.id)
            End If

            Call $"the verification mail to '{email}' could not be sent: {mailError}".warning()
            res.WriteError(HTTP_RFC.RFC_INTERNAL_SERVER_ERROR, $"the verification email could not be sent: {mailError}")
            Return
        End If

        Call $"a verification mail was sent to '{email}' (valid for {config.VerifyTtlMinutes} minutes)".info()

        writeResult(res, True, $"a verification link has been sent to {email}; the link is valid for {config.VerifyTtlMinutes} minutes. " &
                               "open the link, then save the base64 authorization code with 'xGet activate'.",
                    New Dictionary(Of String, Object) From {
            {"email", email},
            {"warning", "check-your-mailbox"}
        })
    End Sub

    ''' <summary>
    ''' the email verification endpoint of the registration flow: the link is
    ''' sent by mail and is valid for a limited time window. when the token is
    ''' valid, the pending registration becomes a real account and the page
    ''' displays the base64 authorization code (email + server url + TOTP
    ''' secret) which the new user saves locally through ``xGet activate``.
    ''' the link is replayable while it is valid.
    ''' </summary>
    <HttpGet("/api/verify")>
    Public Sub VerifyEmail(req As HttpRequest, res As HttpResponse)
        Dim token As String = queryValue(req, "token")
        Dim serverUrl As String = getBaseUrl(req)

        If token.StringEmpty() Then
            Call writeHtml(res, MailService.RenderVerifyFailed(config.TemplateDirectory,
                "the verification link is invalid: the token is missing.", serverUrl))
            Return
        End If

        Dim pending As PendingRegistrationRecord = store.GetPendingRegistration(token)

        If pending Is Nothing Then
            Call "email verification failed: the token was not found".warning()
            Call writeHtml(res, MailService.RenderVerifyFailed(config.TemplateDirectory,
                "the verification link is invalid or it has expired. if the link has expired, " &
                "run 'xGet reset --server " & serverUrl & " --email your@mail.address' to receive a fresh one.", serverUrl))
            Return
        End If

        If pending.IsExpired Then
            Call store.DeletePendingRegistration(pending.id)
            Call $"email verification failed: the token of '{pending.email}' has expired".warning()
            Call writeHtml(res, MailService.RenderVerifyFailed(config.TemplateDirectory,
                "the verification link has expired (the validity window is 30 minutes). " &
                "run 'xGet reset --server " & serverUrl & " --email " & pending.email &
                "' to receive a fresh authorization code.", serverUrl))
            Return
        End If

        ' the pending registration becomes a real account
        If store.GetUser(pending.email) Is Nothing Then
            Call store.CreateUser(pending.email, pending.salt, pending.secret)
            Call $"a new account was verified and activated: {pending.email}".info()
        End If

        ' the pending record is kept until it expires so that the user can open
        ' the mail link again when the authorization code was not copied
        Call store.DeleteExpiredRegistrations()

        ' the base64 authorization payload: email + server url + totp secret
        Dim payloadJson As String = JsonSerializer.Serialize(New Dictionary(Of String, String) From {
            {"email", pending.email},
            {"server", serverUrl},
            {"secret", pending.secret}
        }, JsonOptions)
        Dim payload As String = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson))
        Dim activateCommand As String = $"xGet activate --server {serverUrl} --email {pending.email} --code {payload}"

        Call writeHtml(res, MailService.RenderVerifySuccess(config.TemplateDirectory, pending.email, payload, activateCommand, serverUrl))
    End Sub

    ''' <summary>
    ''' the self service totp secret reset request: a fresh salt/secret pair is
    ''' generated and its activation link is mailed to the account address. the
    ''' old secret keeps working until the reset link is opened.
    ''' </summary>
    <HttpPost("/api/reset")>
    Public Sub RequestSecretReset(req As HttpPOSTRequest, res As HttpResponse)
        Dim email As String = argument(req, "email")

        If String.IsNullOrEmpty(email) Then
            res.WriteError(HTTP_RFC.RFC_BAD_REQUEST, "the email argument is required")
            Return
        End If

        ' ---- rate limit: the reset endpoint is unauthenticated, so one
        ' mailbox and one client ip can start only a few reset requests inside
        ' the configured sliding window (the mailbox flood protection of the
        ' pending reset below can only help after the limit was passed).
        If Not rateLimiter.TryAcquire(
            New RateLimiter.RateLimit(RateLimiter.EmailKey(email), config.ResetEmailLimit),
            New RateLimiter.RateLimit(RateLimiter.IpKey(req.Remote), config.ResetIpLimit)) Then

            Call $"secret reset rejected: rate limit exceeded for '{email}'".warning()
            res.WriteError(HTTP_RFC.RFC_TOO_MANY_REQUEST,
                           "too many reset requests from this address: please wait a few minutes and try again.")
            Return
        End If

        Call store.DeleteExpiredResets()

        If store.GetUser(email) Is Nothing Then
            writeResult(res, False, "this email address is not registered on this server.",
                        New Dictionary(Of String, Object) From {
                {"warning", "not-registered"}
            })
            Return
        End If

        ' throttle the reset mails: one mailbox can not be flooded with them
        If store.HasPendingReset(email) Then
            writeResult(res, True, "a reset mail was already sent to this address and is still valid. " &
                                   "please open the link from your mailbox.",
                        New Dictionary(Of String, Object) From {
                {"warning", "reset-already-pending"}
            })
            Return
        End If

        Dim salt As String = TotpAuth.GenerateSalt(TotpAuth.SaltLength)
        Dim secret As String = TotpModule.Base32Encode(TotpAuth.DeriveSecret(email, salt))
        Dim token As String = generateToken()
        Dim expires As Date = Date.UtcNow.AddMinutes(config.VerifyTtlMinutes)

        Call store.CreatePendingReset(email, token, salt, secret, expires)

        Dim serverUrl As String = getBaseUrl(req)
        Dim resetUrl As String = $"{serverUrl}/api/reset-activate?token={Uri.EscapeDataString(token)}"
        Dim body As String = MailService.RenderResetEmail(config.TemplateDirectory, email, resetUrl, serverUrl, config.VerifyTtlMinutes)
        Dim mailError As String = ""

        If Not MailService.Send(MailService.LoadConfig(store, config.DataDirectory), email,
                                $"reset your nuget authorization on {serverUrl}", body, mailError) Then
            ' the pending record would never be received by the user, so it is
            ' removed right away instead of blocking the mailbox
            Dim orphan As PendingRegistrationRecord = store.GetPendingReset(token)

            If orphan IsNot Nothing Then
                Call store.DeletePendingReset(orphan.id)
            End If

            Call $"the secret reset mail to '{email}' could not be sent: {mailError}".warning()
            res.WriteError(HTTP_RFC.RFC_INTERNAL_SERVER_ERROR, $"the reset email could not be sent: {mailError}")
            Return
        End If

        Call $"a secret reset mail was sent to '{email}' (valid for {config.VerifyTtlMinutes} minutes)".info()

        writeResult(res, True, $"a reset link has been sent to {email}; the link is valid for {config.VerifyTtlMinutes} minutes. " &
                               "open the link, then save the new base64 authorization code with 'xGet activate'.",
                    New Dictionary(Of String, Object) From {
            {"email", email},
            {"warning", "check-your-mailbox"}
        })
    End Sub

    ''' <summary>
    ''' the self service reset activation page: opening the mailed link replaces
    ''' the old totp secret of the account and displays the new base64
    ''' authorization code. the link is replayable while it is valid.
    ''' </summary>
    <HttpGet("/api/reset-activate")>
    Public Sub ResetActivate(req As HttpRequest, res As HttpResponse)
        Dim token As String = queryValue(req, "token")
        Dim serverUrl As String = getBaseUrl(req)

        If token.StringEmpty() Then
            Call writeHtml(res, MailService.RenderVerifyFailed(config.TemplateDirectory,
                "the reset link is invalid: the token is missing.", serverUrl))
            Return
        End If

        Dim pending As PendingRegistrationRecord = store.GetPendingReset(token)

        If pending Is Nothing Then
            Call "secret reset failed: the token was not found".warning()
            Call writeHtml(res, MailService.RenderVerifyFailed(config.TemplateDirectory,
                "the reset link is invalid or it has expired. run 'xGet reset --server " & serverUrl &
                " --email your@mail.address' to receive a fresh one.", serverUrl))
            Return
        End If

        If pending.IsExpired Then
            Call store.DeletePendingReset(pending.id)
            Call $"secret reset failed: the token of '{pending.email}' has expired".warning()
            Call writeHtml(res, MailService.RenderVerifyFailed(config.TemplateDirectory,
                "the reset link has expired (the validity window is 30 minutes). " &
                "run 'xGet reset --server " & serverUrl & " --email " & pending.email &
                "' to receive a fresh authorization code.", serverUrl))
            Return
        End If

        ' the new secret becomes active right away: the old one stops working
        Call store.UpdateUserSecret(pending.email, pending.salt, pending.secret)
        Call $"the totp secret of '{pending.email}' was reset through the self service flow".info()

        Dim payloadJson As String = JsonSerializer.Serialize(New Dictionary(Of String, String) From {
            {"email", pending.email},
            {"server", serverUrl},
            {"secret", pending.secret}
        }, JsonOptions)
        Dim payload As String = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson))
        Dim activateCommand As String = $"xGet activate --server {serverUrl} --email {pending.email} --code {payload}"

        Call writeHtml(res, MailService.RenderVerifySuccess(config.TemplateDirectory, pending.email, payload, activateCommand, serverUrl))
    End Sub


    ''' <summary>
    ''' a cryptographically secure random 256 bit hex verification token.
    ''' </summary>
    Private Shared Function generateToken() As String
        Dim buffer(31) As Byte

        Using rng As RandomNumberGenerator = RandomNumberGenerator.Create()
            rng.GetBytes(buffer)
        End Using

        Dim sb As New StringBuilder(buffer.Length * 2)

        For Each b As Byte In buffer
            sb.Append(b.ToString("x2"))
        Next

        Return sb.ToString()
    End Function

    <HttpPost("/api/v2/package")>
    Public Sub UploadCustom(req As HttpPOSTRequest, res As HttpResponse)
        Call uploadPackage(req, res, argument(req, "email"), argument(req, "code"))
    End Sub

    <HttpPut("/api/v2/package")>
    Public Sub UploadStandard(req As HttpPOSTRequest, res As HttpResponse)
        Dim apiKey As String = req.HttpHeaders.TryGetValue("X-NuGet-ApiKey")
        Dim email As String = ""
        Dim code As String = ""

        If Not String.IsNullOrEmpty(apiKey) Then
            Dim index As Integer = apiKey.IndexOf(":"c)
            If index > 0 Then
                email = apiKey.Substring(0, index)
                code = apiKey.Substring(index + 1)
            End If
        End If

        Call uploadPackage(req, res, email, code)
    End Sub

    Private Sub uploadPackage(req As HttpPOSTRequest, res As HttpResponse, email As String, code As String)
        If Not auth.Authenticate(email, code) Then
            Call $"upload rejected: email='{email}'".warning()
            res.WriteError(HTTP_RFC.RFC_UNAUTHORIZED, "invalid email or TOTP code")
            Return
        End If

        If store.IsUserBanned(email) Then
            Call $"upload rejected: the account '{email}' is banned".warning()
            res.WriteError(HTTP_RFC.RFC_FORBIDDEN, "this account is banned and can not upload packages")
            Return
        End If

        Dim upload As HttpPostedFile = firstUpload(req)

        If upload Is Nothing Then
            res.WriteError(HTTP_RFC.RFC_BAD_REQUEST, "the package file is required (multipart field 'file')")
            Return
        End If

        Dim temp As String = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") & ".nupkg")

        Try
            Call upload.SaveAs(temp)

            ' ---- upload size limit ----
            Dim sizeMB As Double = New FileInfo(temp).Length / 1048576.0

            If sizeMB > config.MaxUploadMB Then
                res.WriteError(HTTP_RFC.RFC_BAD_REQUEST, $"the package size ({sizeMB:0.#} MB) exceeds the configured upload limit of {config.MaxUploadMB:0.#} MB")
                Return
            End If

            Dim metadata As NupkgMetadata
            Try
                metadata = NupkgReader.ReadMetadata(temp)
            Catch ex As Exception
                Call App.LogException(ex)
                res.WriteError(HTTP_RFC.RFC_BAD_REQUEST, $"invalid nupkg package: {ex.Message}")
                Return
            End Try

            If String.IsNullOrEmpty(metadata.Id) OrElse String.IsNullOrEmpty(metadata.Version) Then
                res.WriteError(HTTP_RFC.RFC_BAD_REQUEST, "invalid nuspec: the id or version is missing")
                Return
            End If

            ' ---- package content validation: only managed clr library packages ----
            Dim rejectReason As String = ""

            If Not PackageValidator.Validate(temp, rejectReason) Then
                Call $"package rejected: {metadata.Id} {metadata.Version}: {rejectReason}".warning()
                res.WriteError(HTTP_RFC.RFC_BAD_REQUEST, rejectReason)
                Return
            End If

            If store.PackageExists(metadata.Id, metadata.Version) Then
                res.WriteError(HTTP_RFC.RFC_CONFLICT, $"package {metadata.Id} {metadata.Version} already exists")
                Return
            End If

            ' ---- package ownership: only the owners of an already published
            ' package id may push further versions of it, so that no account can
            ' hijack the namespace of a package of another publisher. a brand
            ' new package id can be registered by any verified account. the id
            ' is resolved to its stored spelling first, so the owner lookup
            ' can not be bypassed through the case sensitivity of the id.
            Dim storedId As String = store.ResolvePackageId(metadata.Id)

            If Not storedId.StringEmpty() AndAlso Not isPackageOwner(storedId, email) Then
                Call $"upload rejected: '{email}' is not an owner of '{storedId}'".warning()
                res.WriteError(HTTP_RFC.RFC_FORBIDDEN,
                    $"the account '{email}' is not an owner of the package '{storedId}', so it can not publish new versions of it")
                Return
            End If

            Dim pkg As New PackageRecord With {
                .package_id = metadata.Id,
                .version = metadata.Version,
                .description = metadata.Description,
                .authors = metadata.Authors,
                .tags = metadata.Tags,
                .project_url = metadata.ProjectUrl,
                .license = metadata.License,
                .dependencies = metadata.Dependencies,
                .downloads = 0,
                .size = New FileInfo(temp).Length,
                .sha256 = computeSha256(temp),
                .published = Date.UtcNow,
                .listed = True
            }

            ' the files are only written to their final location after all the
            ' validations passed, so a rejected upload leaves no half product.
            Call Directory.CreateDirectory(versionDirectory(pkg))
            Call File.Copy(temp, nupkgFilePath(pkg), overwrite:=True)
            Call File.WriteAllText(nuspecFilePath(pkg), NupkgReader.ReadNuspecXml(temp))
            Call store.AddPackage(pkg)
            Call store.RecordUploader(pkg.package_id, pkg.version, email)
            Call registerStaticFiles(pkg)
            Call indexPackage(pkg, metadata)
            Call refreshStatistics()
            Call indexApiDocs(pkg)

            Call writeResult(res, True, $"published {pkg.package_id} {pkg.version}", New Dictionary(Of String, Object) From {
                {"id", pkg.package_id},
                {"version", pkg.version},
                {"size", pkg.size},
                {"sha256", pkg.sha256}
            })
        Catch ex As Exception
            Call App.LogException(ex)
            res.WriteError(HTTP_RFC.RFC_INTERNAL_SERVER_ERROR, ex.Message)
        Finally
            Try
                If File.Exists(temp) Then
                    File.Delete(temp)
                End If
            Catch
            End Try
        End Try
    End Sub

#End Region

#Region "package flags (obsolete / hidden)"

    ''' <summary>
    ''' mark (or unmark) a package id as obsolete. the operation requires the
    ''' TOTP credentials of the account which uploaded the package: the
    ''' ``obsolete`` marker means that the package is no longer recommended,
    ''' while it is still served and documented. the server administrator can
    ''' set the very same flag directly through the ``xConsole package obsolete``
    ''' command.
    ''' </summary>
    <HttpPost("/api/package/obsolete")>
    Public Sub ApiPackageObsolete(req As HttpPOSTRequest, res As HttpResponse)
        Call setPackageFlag(req, res, "obsolete")
    End Sub

    ''' <summary>
    ''' hide (or unhide) a package id from every public view of the server. only
    ''' the uploader account of the package (or a server administrator) may do it
    ''' through the api; the administrator manages the very same flag directly
    ''' through the ``xConsole package hide`` command.
    ''' </summary>
    <HttpPost("/api/package/hide")>
    Public Sub ApiPackageHide(req As HttpPOSTRequest, res As HttpResponse)
        Call setPackageFlag(req, res, "hidden")
    End Sub

    ''' <summary>
    ''' the shared implementation of the two package flag endpoints. the request
    ''' carries the TOTP credentials, the package id and an optional ``value``
    ''' argument (``true``/``false``, default <c>True</c>).
    ''' </summary>
    ''' <param name="req"></param>
    ''' <param name="res"></param>
    ''' <param name="flagName">either ``obsolete`` or ``hidden``.</param>
    Private Sub setPackageFlag(req As HttpPOSTRequest, res As HttpResponse, flagName As String)
        Dim email As String = argument(req, "email")
        Dim code As String = argument(req, "code")

        If Not auth.Authenticate(email, code) Then
            Call $"package {flagName} rejected: email='{email}'".warning()
            res.WriteError(HTTP_RFC.RFC_UNAUTHORIZED, "invalid email or TOTP code")
            Return
        End If

        Dim id As String = argument(req, "id")

        If id.StringEmpty() Then
            res.WriteError(HTTP_RFC.RFC_BAD_REQUEST, "the package id argument is required")
            Return
        End If

        id = id.Trim()

        ' switch to the stored spelling of the id, so that the owner lookup below
        ' is not affected by the case sensitivity of the JSql string comparison
        Dim storedId As String = store.ResolvePackageId(id)

        If storedId.StringEmpty() Then
            res.WriteError(HTTP_RFC.RFC_NOT_FOUND, $"package '{id}' was not found")
            Return
        End If

        id = storedId

        If Not canManagePackage(id, email) Then
            Call $"package {flagName} rejected: '{email}' is not an owner of '{id}'".warning()
            res.WriteError(HTTP_RFC.RFC_FORBIDDEN,
                $"the account '{email}' is not an owner of the package '{id}', so it can not change its {flagName} state")
            Return
        End If

        Dim flag As Boolean = parseFlagArgument(argument(req, "value"))
        Call store.SetPackageFlag(id, flagName, flag)

        If flagName = "hidden" Then
            ' the cached views of the feed have to follow the new state
            Call refreshHiddenPackageViews(id)
        End If

        Dim flags As PackageFlagRecord = store.GetPackageFlags(id)

        Call $"package '{id}' {flagName} flag set to {flag} by '{email}'".info()

        writeResult(res, True, packageFlagMessage(id, flagName, flag), New Dictionary(Of String, Object) From {
            {"id", id},
            {"obsolete", flags.obsolete},
            {"hidden", flags.hidden},
            {"owner", store.GetPackageUploader(id)}
        })
    End Sub

    ''' <summary>
    ''' may the given account publish new versions of the given package id? the
    ''' owners of a package are: the accounts which uploaded one of its
    ''' versions, the accounts which the server administrator has manually
    ''' assigned through the ``xConsole package owner`` command, and an account
    ''' which carries the ``official`` badge acts as a server administrator.
    ''' </summary>
    ''' <param name="packageId">the package id.</param>
    ''' <param name="email">the authenticated account.</param>
    Private Function isPackageOwner(packageId As String, email As String) As Boolean
        If String.IsNullOrEmpty(packageId) OrElse String.IsNullOrEmpty(email) Then
            Return False
        End If

        ' the official badge is the server administrator role
        If store.IsUserOfficial(email) Then
            Return True
        End If

        Dim mail As String = email.Trim()

        ' the accounts which uploaded any recorded version of the package
        If store.GetPackageUploaders(packageId).Any(Function(e) e.Trim().Equals(mail, StringComparison.OrdinalIgnoreCase)) Then
            Return True
        End If

        ' the accounts which the administrator assigned through xConsole
        If store.GetPackageOwners(packageId).Any(Function(e) e.Trim().Equals(mail, StringComparison.OrdinalIgnoreCase)) Then
            Return True
        End If

        Return False
    End Function

    ''' <summary>
    ''' may the given account change the public flags of the given package? the
    ''' owners of the package (see <see cref="isPackageOwner"/>) may do it.
    ''' </summary>
    ''' <param name="packageId">the package id.</param>
    ''' <param name="email">the authenticated account.</param>
    Private Function canManagePackage(packageId As String, email As String) As Boolean
        Return isPackageOwner(packageId, email)
    End Function

    ''' <summary>
    ''' read the state of a package flag from the request: an absent or
    ''' unrecognized value means <c>True</c>, so that the plain command marks the
    ''' package, while ``false``/``off``/``no``/``0`` clears the flag again.
    ''' </summary>
    Private Shared Function parseFlagArgument(text As String) As Boolean
        Select Case If(text, "").Trim().ToLowerInvariant()
            Case "false", "off", "no", "0" : Return False
            Case Else : Return True
        End Select
    End Function

    ''' <summary>
    ''' the human readable confirmation message of one package flag change.
    ''' </summary>
    Private Shared Function packageFlagMessage(packageId As String, flagName As String, flag As Boolean) As String
        If flagName = "obsolete" Then
            If flag Then
                Return $"the package '{packageId}' is now marked as obsolete."
            Else
                Return $"the package '{packageId}' is no longer marked as obsolete."
            End If
        End If

        If flag Then
            Return $"the package '{packageId}' is now hidden: it is not listed, searchable, downloadable nor documented anymore."
        Else
            Return $"the package '{packageId}' is visible again."
        End If
    End Function

    ''' <summary>
    ''' refresh every cached view of the feed after the hidden state of a package
    ''' changed: the static file mappings of the package, the precomputed
    ''' statistics and the site map.
    ''' </summary>
    ''' <param name="packageId">the package id whose flag changed.</param>
    Private Sub refreshHiddenPackageViews(packageId As String)
        Call refreshStaticFiles(packageId)
        Call refreshStatistics()

        ' the document database did not change, but the set of the public urls
        ' did, so the sitemap is rebuilt on the next idle window
        If sitemap IsNot Nothing Then
            Call sitemap.NotifyDocsChanged()
        End If
    End Sub

    ''' <summary>
    ''' withdraw the static file mappings of every package which is hidden at the
    ''' server start. the host attaches the physical package folder to the
    ''' ``/packages/`` urls before this module is mounted, so the sanitizing has
    ''' to run here as well and not only when a flag is changed at runtime.
    ''' </summary>
    Private Sub applyHiddenStaticFiles()
        If router Is Nothing OrElse router.FileSystem Is Nothing Then
            Return
        End If

        Dim hidden As HashSet(Of String) = store.GetHiddenPackageIds()

        For Each id As String In hidden
            Call unregisterStaticFiles(id)
        Next

        If hidden.Count > 0 Then
            Call $"{hidden.Count} hidden package(s): their static package files were withdrawn".info()
        End If
    End Sub

    ''' <summary>
    ''' bring the static file mappings of one package in line with its hidden
    ''' flag: a hidden package is withdrawn from the static file system, and a
    ''' package which became visible again gets its mappings back.
    ''' </summary>
    ''' <param name="packageId">the package id.</param>
    Private Sub refreshStaticFiles(packageId As String)
        If router Is Nothing OrElse router.FileSystem Is Nothing Then
            Return
        End If

        Dim key As String = If(packageId, "").Trim()

        If key.StringEmpty() Then
            Return
        End If

        If store.IsPackageHidden(key) Then
            Call unregisterStaticFiles(key)
            Return
        End If

        For Each pkg As PackageRecord In store.ReadAllPackages()
            If pkg.package_id IsNot Nothing AndAlso pkg.package_id.Equals(key, StringComparison.OrdinalIgnoreCase) Then
                Call registerStaticFiles(pkg)
            End If
        Next
    End Sub

    ''' <summary>
    ''' withdraw the static file mappings of one package.
    ''' </summary>
    ''' <remarks>
    ''' the host consults its file system listener before the controller routes,
    ''' and the mapping table of the listener has no removal entry point, so each
    ''' recorded url is re-mapped onto a file which does not exist: the listener
    ''' then reports the resource as missing, the request falls through to the
    ''' controller and no hidden package file can leak through ``/packages/...``
    ''' while the server keeps running.
    ''' </remarks>
    ''' <param name="packageId">the package id.</param>
    Private Sub unregisterStaticFiles(packageId As String)
        If router Is Nothing OrElse router.FileSystem Is Nothing Then
            Return
        End If

        Dim key As String = If(packageId, "").Trim()
        If key.StringEmpty() Then
            Return
        End If

        Dim fs As Flute.Http.FileSystem.FileSystem = router.FileSystem.fs(0)
        Dim idLower As String = key.ToLowerInvariant()
        Dim missing As String = Path.Combine(config.TempDirectory, ".unmapped-package")

        For Each pkg As PackageRecord In store.ReadAllPackages()
            If pkg.package_id Is Nothing OrElse Not pkg.package_id.Equals(key, StringComparison.OrdinalIgnoreCase) Then
                Continue For
            End If

            Dim versionLower As String = If(pkg.version, "").ToLowerInvariant()

            Call fs.AddMapping($"/packages/{idLower}/{versionLower}/{idLower}.{versionLower}.nupkg", missing)
            Call fs.AddMapping($"/packages/{idLower}/{versionLower}/{idLower}.nuspec", missing)
        Next
    End Sub

#End Region

#Region "web front end json api"

    <HttpGet("/api/packages")>
    Public Sub ApiPackages(req As HttpRequest, res As HttpResponse)
        Dim keyword As String = queryValue(req, "q")
        Dim skip As Integer = queryInt(req, "skip", 0)
        Dim take As Integer = queryInt(req, "take", 50)

        Dim all As List(Of PackageSummary) = store.ListPackages(keyword)
        Dim page As List(Of PackageSummary) = all.Skip(skip).Take(take).ToList()

        res.AccessControlAllowOrigin = "*"
        writeJson(res, New Dictionary(Of String, Object) From {
            {"total", all.Count},
            {"skip", skip},
            {"take", take},
            {"packages", page.Select(Function(p) packageSummaryJson(p)).ToList()}
        })
    End Sub

    <HttpGet("/api/stats")>
    Public Sub ApiStats(req As HttpRequest, res As HttpResponse)
        Dim stats As NugetStats = store.Stats()
        Dim groups As List(Of PackageSearchResult) = store.GroupPackages("")

        Dim top As New List(Of Object)
        For Each group As PackageSearchResult In groups.OrderByDescending(Function(g) g.total_downloads).Take(10)
            top.Add(New Dictionary(Of String, Object) From {
                {"id", group.package_id},
                {"latestVersion", group.latest.version},
                {"downloads", group.total_downloads},
                {"versions", group.versions.Count}
            })
        Next

        Dim recent As New List(Of Object)
        For Each pkg As PackageRecord In store.ReadVisiblePackages().OrderByDescending(Function(p) p.published).Take(10)
            recent.Add(New Dictionary(Of String, Object) From {
                {"id", pkg.package_id},
                {"version", pkg.version},
                {"published", isoDate(pkg.published)},
                {"downloads", pkg.downloads}
            })
        Next

        res.AccessControlAllowOrigin = "*"
        writeJson(res, New Dictionary(Of String, Object) From {
            {"stats", New Dictionary(Of String, Object) From {
                {"packages", stats.packages},
                {"versions", stats.versions},
                {"downloads", stats.downloads},
                {"users", stats.users},
                {"views", stats.views},
                {"docViews", stats.docViews}
            }},
            {"topDownloads", top},
            {"recent", recent},
            {"generated", isoDate(Date.UtcNow)}
        })
    End Sub

    <HttpGet("/api/package/{id}")>
    Public Sub ApiPackage(req As HttpRequest, res As HttpResponse)
        Call writePackageDetail(req, res, routeValue(req, "id"), Nothing)
    End Sub

    <HttpGet("/api/package/{id}/{version}")>
    Public Sub ApiPackageVersion(req As HttpRequest, res As HttpResponse)
        Call writePackageDetail(req, res, routeValue(req, "id"), routeValue(req, "version"))
    End Sub

    Private Sub writePackageDetail(req As HttpRequest, res As HttpResponse, id As String, version As String)
        Dim versions As List(Of PackageRecord) = store.GetVersions(id)

        If versions.Count = 0 Then
            res.WriteError(HTTP_RFC.RFC_NOT_FOUND, $"package '{id}' was not found")
            Return
        End If

        Dim latest As PackageRecord = If(String.IsNullOrEmpty(version), versions.Last(),
            versions.FirstOrDefault(Function(v) v.version.Equals(version, StringComparison.OrdinalIgnoreCase)))

        If latest Is Nothing Then
            res.WriteError(HTTP_RFC.RFC_NOT_FOUND, $"package '{id}' {version} was not found")
            Return
        End If

        Dim baseUrl As String = getBaseUrl(req)
        Dim idLower As String = latest.package_id.ToLowerInvariant()
        Dim versionList As New List(Of Object)

        For Each item As PackageRecord In versions
            Dim versionLower As String = item.version.ToLowerInvariant()
            versionList.Add(New Dictionary(Of String, Object) From {
                {"version", item.version},
                {"downloads", item.downloads},
                {"size", item.size},
                {"published", isoDate(item.published)},
                {"listed", item.listed},
                {"downloadUrl", $"{baseUrl}/v3-flatcontainer/{idLower}/{versionLower}/{idLower}.{versionLower}.nupkg"},
                {"nuspecUrl", $"{baseUrl}/v3-flatcontainer/{idLower}/{versionLower}/{idLower}.nuspec"}
            })
        Next

        Dim metadata As Dictionary(Of String, String) = store.GetPackageMetadata(latest.package_id)

        ' the uploader account of this package version (falls back to the latest
        ' recorded version of the package) and its official badge state
        Dim uploader As String = store.GetUploader(latest.package_id, latest.version)

        If uploader.StringEmpty() Then
            uploader = store.GetPackageUploader(latest.package_id)
        End If

        Dim uploaderFlags As UserFlagRecord = store.GetUserFlags(uploader)
        Dim uploaderOfficial As Boolean = uploaderFlags.official

        Dim iconFile As String = ""
        If metadata.TryGetValue("iconFile", iconFile) AndAlso Not String.IsNullOrEmpty(iconFile) Then
            ' the icon is served by the controller endpoint
        Else
            iconFile = ""
        End If

        Dim dependencies As New List(Of Object)
        For Each dependency As NuspecDependency In store.GetPackageDependencies(latest.package_id)
            Dim hosted As Boolean = store.PackageExists(dependency.id)
            dependencies.Add(New Dictionary(Of String, Object) From {
                {"id", dependency.id},
                {"range", dependency.range},
                {"targetFramework", dependency.targetFramework},
                {"hosted", hosted},
                {"url", If(hosted, "package.html?id=" & Uri.EscapeDataString(dependency.id),
                           "https://www.nuget.org/packages/" & Uri.EscapeDataString(dependency.id))}
            })
        Next

        Dim metadataJson As New Dictionary(Of String, Object)
        For Each item In metadata
            metadataJson(item.Key) = item.Value
        Next

        ' count the detail page view of the current utc day
        Call store.RecordView(latest.package_id)

        res.AccessControlAllowOrigin = "*"
        writeJson(res, New Dictionary(Of String, Object) From {
            {"id", latest.package_id},
            {"title", fieldValue(metadata, "title")},
            {"description", latest.description},
            {"summary", fieldValue(metadata, "summary")},
            {"releaseNotes", fieldValue(metadata, "releaseNotes")},
            {"authors", latest.authors},
            {"owners", fieldValue(metadata, "owners")},
            {"copyright", fieldValue(metadata, "copyright")},
            {"language", fieldValue(metadata, "language")},
            {"tags", splitTags(latest.tags)},
            {"license", latest.license},
            {"licenseUrl", fieldValue(metadata, "licenseUrl")},
            {"projectUrl", latest.project_url},
            {"repository", fieldValue(metadata, "repository")},
            {"requireLicenseAcceptance", fieldValue(metadata, "requireLicenseAcceptance")},
            {"latestVersion", versions.Last().version},
            {"selectedVersion", latest.version},
            {"totalDownloads", versions.Sum(Function(v) v.downloads)},
            {"published", isoDate(latest.published)},
            {"obsolete", store.IsPackageObsolete(latest.package_id)},
            {"uploader", uploader},
            {"uploaderOfficial", uploaderOfficial},
            {"uploaderDemo", uploaderFlags.demo},
            {"uploaderBanned", uploaderFlags.banned},
            {"iconUrl", If(String.IsNullOrEmpty(iconFile), "", $"{baseUrl}/api/icon/{Uri.EscapeDataString(latest.package_id)}")},
            {"readme", readmeInfo(baseUrl, latest)},
            {"docs", docsInfo(latest)},
            {"cluster", clusterInfo(latest.package_id)},
            {"metadata", metadataJson},
            {"dependencies", dependencies},
            {"versions", versionList}
        })
    End Sub

    ''' <summary>
    ''' describe the readme document of one package version for the web front
    ''' end. the document body is intentionally not inlined; the client fetches
    ''' it from <see cref="ApiPackageReadme(HttpRequest, HttpResponse)"/> on
    ''' demand.
    ''' </summary>
    Private Function readmeInfo(baseUrl As String, pkg As PackageRecord) As Dictionary(Of String, Object)
        Dim file As String = findReadmeFile(pkg)

        If String.IsNullOrEmpty(file) Then
            Return New Dictionary(Of String, Object) From {{"available", False}}
        End If

        Dim extension As String = Path.GetExtension(file).TrimStart("."c).ToLowerInvariant()
        If String.IsNullOrEmpty(extension) Then
            extension = "md"
        End If

        Dim idLower As String = pkg.package_id.ToLowerInvariant()
        Dim versionLower As String = pkg.version.ToLowerInvariant()

        Return New Dictionary(Of String, Object) From {
            {"available", True},
            {"file", Path.GetFileName(file)},
            {"format", extension},
            {"markdown", extension = "md" OrElse extension = "markdown"},
            {"url", $"{baseUrl}/api/readme/{Uri.EscapeDataString(idLower)}/{Uri.EscapeDataString(versionLower)}"}
        }
    End Function

    ''' <summary>
    ''' describe the api document of one package version for the web front end:
    ''' the link to the per package api document index page. When the selected
    ''' version ships no document, the latest version which has one is linked
    ''' instead.
    ''' </summary>
    ''' <param name="pkg"></param>
    ''' <returns></returns>
    Private Function docsInfo(pkg As PackageRecord) As Dictionary(Of String, Object)
        Dim version As String = pkg.version

        If Not store.HasPackageApiDocs(pkg.package_id, version) Then
            Dim versions As List(Of String) = store.GetPackageApiDocVersions(pkg.package_id)

            If versions.Count = 0 Then
                Return New Dictionary(Of String, Object) From {{"available", False}}
            End If

            version = versions.Last()
        End If

        Dim types As List(Of PackageApiDocRecord) = store.ReadPackageApiDocIndex(pkg.package_id, version)

        Return New Dictionary(Of String, Object) From {
            {"available", True},
            {"version", version},
            {"typeCount", types.Count},
            {"url", ApiDocPages.PackageIndexUrl(pkg.package_id, version)}
        }
    End Function

    ''' <summary>
    ''' the umap coordinate and the kmeans label of one package as produced by
    ''' the periodic tag space analysis; <c>available=False</c> when the package
    ''' was not part of the last analysis (for example a package without tags).
    ''' </summary>
    Private Function clusterInfo(packageId As String) As Dictionary(Of String, Object)
        Dim record As PackageClusterRecord = store.GetPackageCluster(packageId)

        If record Is Nothing Then
            Return New Dictionary(Of String, Object) From {{"available", False}}
        End If

        Return New Dictionary(Of String, Object) From {
            {"available", True},
            {"label", record.cluster},
            {"x", record.x},
            {"y", record.y},
            {"z", record.z},
            {"updated", isoDate(record.updated)}
        }
    End Function

    ''' <summary>
    ''' locate the readme document of one package version inside its version
    ''' directory; returns <c>Nothing</c> when the package ships no readme.
    ''' </summary>
    Private Function findReadmeFile(pkg As PackageRecord) As String
        If pkg Is Nothing Then
            Return Nothing
        End If

        Dim folder As String = versionDirectory(pkg)
        If Not folder.DirectoryExists Then
            Return Nothing
        End If

        ' the file name is recorded by the upload, but fall back to a directory
        ' scan so that a readme uploaded by an older build is still served.
        Dim metadata As Dictionary(Of String, String) = store.GetPackageMetadata(pkg.package_id, pkg.version)
        Dim readmeFile As String = fieldValue(metadata, "readmeFile")

        If Not readmeFile.StringEmpty Then
            Dim recorded As String = Path.Combine(folder, readmeFile)
            If File.Exists(recorded) Then
                Return recorded
            End If
        End If

        Return Directory.GetFiles(folder, "readme.*") _
            .OrderBy(Function(f) f, StringComparer.OrdinalIgnoreCase) _
            .FirstOrDefault()
    End Function

    <HttpGet("/api/readme/{id}")>
    Public Sub ApiPackageReadme(req As HttpRequest, res As HttpResponse)
        Call writeReadme(res, routeValue(req, "id"), Nothing)
    End Sub

    <HttpGet("/api/readme/{id}/{version}")>
    Public Sub ApiPackageReadmeVersion(req As HttpRequest, res As HttpResponse)
        Call writeReadme(res, routeValue(req, "id"), routeValue(req, "version"))
    End Sub

    ''' <summary>
    ''' stream the readme document of a package version as plain text (or
    ''' markdown) so that the detail page can render it.
    ''' </summary>
    Private Sub writeReadme(res As HttpResponse, id As String, version As String)
        Call SitemapScheduler.TouchRequest()

        Dim versions As List(Of PackageRecord) = store.GetVersions(id)

        If versions.Count = 0 Then
            res.WriteError(HTTP_RFC.RFC_NOT_FOUND, $"package '{id}' was not found")
            Return
        End If

        Dim pkg As PackageRecord = If(String.IsNullOrEmpty(version), versions.Last(),
            versions.FirstOrDefault(Function(v) v.version.Equals(version, StringComparison.OrdinalIgnoreCase)))

        If pkg Is Nothing Then
            res.WriteError(HTTP_RFC.RFC_NOT_FOUND, $"package '{id}' {version} was not found")
            Return
        End If

        Dim readmePath As String = findReadmeFile(pkg)

        If readmePath.StringEmpty OrElse Not File.Exists(readmePath) Then
            res.WriteError(HTTP_RFC.RFC_NOT_FOUND, "this package version ships no readme document")
            Return
        End If

        Dim extension As String = Path.GetExtension(readmePath).TrimStart("."c).ToLowerInvariant()
        Dim mime As String = If(extension = "md" OrElse extension = "markdown",
            "text/markdown; charset=utf-8", "text/plain; charset=utf-8")

        Dim bytes As Byte() = File.ReadAllBytes(readmePath)

        res.AccessControlAllowOrigin = "*"
        res.WriteContent(bytes, mime)
    End Sub

    Private Shared Function fieldValue(metadata As Dictionary(Of String, String), name As String) As String
        Dim value As String = Nothing
        If metadata IsNot Nothing AndAlso metadata.TryGetValue(name, value) Then
            Return value
        End If
        Return ""
    End Function

    <HttpGet("/api/icon/{id}")>
    Public Sub ApiPackageIcon(req As HttpRequest, res As HttpResponse)
        Call SitemapScheduler.TouchRequest()

        Dim id As String = routeValue(req, "id")
        Dim metadata As Dictionary(Of String, String) = store.GetPackageMetadata(id)
        Dim iconFile As String = ""
        Dim pkg As PackageRecord = store.GetVersions(id).LastOrDefault()

        If pkg Is Nothing OrElse Not metadata.TryGetValue("iconFile", iconFile) OrElse iconFile.StringEmpty() Then
            res.WriteError(HTTP_RFC.RFC_NOT_FOUND, "no icon image for this package")
            Return
        End If

        Dim iconPath As String = Path.Combine(versionDirectory(pkg), iconFile)
        If Not File.Exists(iconPath) Then
            res.WriteError(HTTP_RFC.RFC_NOT_FOUND, "no icon image for this package")
            Return
        End If

        res.AccessControlAllowOrigin = "*"
        res.SendFile(iconPath)
    End Sub

    <HttpGet("/api/tag/{tag}")>
    Public Sub ApiTagPackages(req As HttpRequest, res As HttpResponse)
        Dim tag As String = routeValue(req, "tag")
        Dim skip As Integer = queryInt(req, "skip", 0)
        Dim take As Integer = queryInt(req, "take", 20)
        Dim total As Integer = 0

        Dim page As List(Of PackageSummary) = store.GetPackagesByTag(tag, skip, take, total)

        res.AccessControlAllowOrigin = "*"
        writeJson(res, New Dictionary(Of String, Object) From {
            {"tag", tag},
            {"total", total},
            {"skip", skip},
            {"take", take},
            {"packages", page.Select(Function(p) packageSummaryJson(p)).ToList()}
        })
    End Sub

    Private Shared Function packageSummaryJson(pkg As PackageSummary) As Dictionary(Of String, Object)
        Return New Dictionary(Of String, Object) From {
            {"id", pkg.package_id},
            {"latestVersion", pkg.latest_version},
            {"description", pkg.description},
            {"authors", pkg.authors},
            {"tags", pkg.tags},
            {"license", pkg.license},
            {"projectUrl", pkg.project_url},
            {"totalDownloads", pkg.total_downloads},
            {"versions", pkg.versions},
            {"published", isoDate(pkg.published)},
            {"obsolete", pkg.obsolete}
        }
    End Function

#End Region

#Region "daily activity"

    ''' <summary>
    ''' the daily download / page view series of a single package.
    ''' </summary>
    <HttpGet("/api/activity/package/{id}")>
    Public Sub ApiPackageActivity(req As HttpRequest, res As HttpResponse)
        Dim id As String = routeValue(req, "id")
        Dim days As Integer = clampDays(req)

        If store.GetVersions(id).Count = 0 Then
            res.WriteError(HTTP_RFC.RFC_NOT_FOUND, $"package '{id}' was not found")
            Return
        End If

        Dim activity As List(Of DailyActivity) = store.GetPackageActivity(id, days)

        res.AccessControlAllowOrigin = "*"
        writeJson(res, New Dictionary(Of String, Object) From {
            {"id", id},
            {"days", days},
            {"totalDownloads", activity.Sum(Function(a) a.downloads)},
            {"totalViews", activity.Sum(Function(a) a.views)},
            {"totalDocViews", activity.Sum(Function(a) a.docViews)},
            {"points", activitySeries(activity, days)}
        })
    End Sub

    ''' <summary>
    ''' the daily download / page view series summed over the whole feed.
    ''' </summary>
    <HttpGet("/api/activity/feed")>
    Public Sub ApiFeedActivity(req As HttpRequest, res As HttpResponse)
        Dim days As Integer = clampDays(req)
        Dim activity As List(Of DailyActivity) = store.GetFeedActivity(days)

        res.AccessControlAllowOrigin = "*"
        writeJson(res, New Dictionary(Of String, Object) From {
            {"days", days},
            {"totalDownloads", activity.Sum(Function(a) a.downloads)},
            {"totalViews", activity.Sum(Function(a) a.views)},
            {"totalDocViews", activity.Sum(Function(a) a.docViews)},
            {"points", activitySeries(activity, days)}
        })
    End Sub

    ''' <summary>
    ''' read and clamp the ``days`` query parameter of the activity endpoints.
    ''' </summary>
    Private Shared Function clampDays(req As HttpRequest) As Integer
        Dim days As Integer = queryInt(req, "days", 30)

        If days < 1 Then
            Return 1
        ElseIf days > 365 Then
            Return 365
        Else
            Return days
        End If
    End Function

    ''' <summary>
    ''' expand the recorded activity into a continuous per day series, filling
    ''' the days without traffic with zeros so that the chart has a stable time
    ''' axis.
    ''' </summary>
    Private Shared Function activitySeries(activity As List(Of DailyActivity), days As Integer) As List(Of Object)
        Dim table As New Dictionary(Of String, DailyActivity)(StringComparer.Ordinal)

        For Each item As DailyActivity In activity
            table(item.day) = item
        Next

        Dim points As New List(Of Object)
        Dim today As Date = Date.UtcNow

        For offset As Integer = days - 1 To 0 Step -1
            Dim day As String = NugetStore.DayKey(today.AddDays(-offset))
            Dim item As DailyActivity = Nothing

            If table.TryGetValue(day, item) Then
                points.Add(New Dictionary(Of String, Object) From {
                    {"day", day},
                    {"downloads", item.downloads},
                    {"views", item.views},
                    {"docViews", item.docViews}
                })
            Else
                points.Add(New Dictionary(Of String, Object) From {
                    {"day", day},
                    {"downloads", 0},
                    {"views", 0},
                    {"docViews", 0}
                })
            End If
        Next

        Return points
    End Function

#End Region

#Region "reverse dependencies and user pages"

    ''' <summary>
    ''' every package of the feed that depends on the given package (the reverse
    ''' dependency view of the ``dependents.html`` page).
    ''' </summary>
    <HttpGet("/api/dependents/{id}")>
    Public Sub ApiPackageDependents(req As HttpRequest, res As HttpResponse)
        Dim id As String = routeValue(req, "id")

        If store.GetVersions(id).Count = 0 Then
            res.WriteError(HTTP_RFC.RFC_NOT_FOUND, $"package '{id}' was not found")
            Return
        End If

        Dim dependents As List(Of NuspecDependency) = store.GetPackageDependents(id)
        Dim summaries As Dictionary(Of String, PackageSummary) = store.ListPackages("") _
            .GroupBy(Function(p) p.package_id, StringComparer.OrdinalIgnoreCase) _
            .ToDictionary(Function(g) g.Key, Function(g) g.First(), StringComparer.OrdinalIgnoreCase)

        Dim items As New List(Of Object)

        For Each item As NuspecDependency In dependents
            Dim summary As PackageSummary = Nothing
            summaries.TryGetValue(item.id, summary)

            items.Add(New Dictionary(Of String, Object) From {
                {"id", item.id},
                {"latestVersion", If(summary IsNot Nothing, summary.latest_version, "")},
                {"downloads", If(summary IsNot Nothing, summary.total_downloads, 0L)},
                {"versionRange", If(item.range, "")},
                {"url", $"package.html?id={Uri.EscapeDataString(item.id)}"}
            })
        Next

        res.AccessControlAllowOrigin = "*"
        writeJson(res, New Dictionary(Of String, Object) From {
            {"id", id},
            {"total", items.Count},
            {"dependents", items}
        })
    End Sub

    ''' <summary>
    ''' the profile of one uploader account: its admin flags, the packages that
    ''' it uploaded and the distinct project urls of those packages. this feeds
    ''' the ``user.html`` page.
    ''' </summary>
    <HttpGet("/api/user/{email}")>
    Public Sub ApiUserProfile(req As HttpRequest, res As HttpResponse)
        Dim email As String = routeValue(req, "email")

        If String.IsNullOrEmpty(email) OrElse Not email.Contains("@") Then
            res.WriteError(HTTP_RFC.RFC_BAD_REQUEST, "a valid email address is expected")
            Return
        End If

        email = email.Trim().ToLowerInvariant()
        Dim flags As UserFlagRecord = store.GetUserFlags(email)

        ' an email which never registered an account and which uploaded no
        ' package of this feed has no profile page
        If Not accountExists(email) Then
            res.WriteError(HTTP_RFC.RFC_NOT_FOUND, $"the account '{email}' was not found")
            Return
        End If

        Dim packages As List(Of PackageSummary) = store.GetUserPackages(email)
        Dim projects As List(Of ProjectInfo) = store.GetUserProjects(email)

        res.AccessControlAllowOrigin = "*"
        writeJson(res, New Dictionary(Of String, Object) From {
            {"email", email},
            {"flags", New Dictionary(Of String, Object) From {
                {"official", flags.official},
                {"demo", flags.demo},
                {"banned", flags.banned}
            }},
            {"packageCount", packages.Count},
            {"totalDownloads", packages.Sum(Function(p) p.total_downloads)},
            {"packages", packages.Select(AddressOf packageSummaryJson).ToList()},
            {"projects", projects.Select(Function(p) CObj(New Dictionary(Of String, Object) From {
                {"url", p.url},
                {"host", p.host},
                {"packageCount", p.packageCount}
            })).ToList()}
        })
    End Sub

    ''' <summary>
    ''' test whether the given email has a profile on this server: a registered
    ''' account, or at least one package which it uploaded.
    ''' </summary>
    ''' <param name="email">the email address (compared case insensitively).</param>
    Private Function accountExists(email As String) As Boolean
        If String.IsNullOrEmpty(email) Then
            Return False
        End If

        If store.GetUser(email) IsNot Nothing Then
            Return True
        End If

        Return store.GetUserPackages(email).Count > 0
    End Function

    ''' <summary>
    ''' the daily download / page view / doc view series aggregated over every
    ''' package that the given account has uploaded.
    ''' </summary>
    <HttpGet("/api/activity/user/{email}")>
    Public Sub ApiUserActivity(req As HttpRequest, res As HttpResponse)
        Dim email As String = routeValue(req, "email")

        If String.IsNullOrEmpty(email) Then
            res.WriteError(HTTP_RFC.RFC_BAD_REQUEST, "the email route value is required")
            Return
        End If

        Dim days As Integer = clampDays(req)

        If Not accountExists(email) Then
            res.WriteError(HTTP_RFC.RFC_NOT_FOUND, $"the account '{email}' was not found")
            Return
        End If

        Dim activity As List(Of DailyActivity) = store.GetUserActivityAggregate(email.Trim().ToLowerInvariant(), days)

        res.AccessControlAllowOrigin = "*"
        writeJson(res, New Dictionary(Of String, Object) From {
            {"email", email},
            {"days", days},
            {"totalDownloads", activity.Sum(Function(a) a.downloads)},
            {"totalViews", activity.Sum(Function(a) a.views)},
            {"totalDocViews", activity.Sum(Function(a) a.docViews)},
            {"points", activitySeries(activity, days)}
        })
    End Sub

#End Region

#Region "statistics"

    <HttpGet("/api/stats/tags")>
    Public Sub ApiStatsTags(req As HttpRequest, res As HttpResponse)
        Call writeStatistic(res, NugetStatistics.TagsStatName, Function() NugetStatistics.BuildTags(store.ReadVisiblePackages()))
    End Sub

    <HttpGet("/api/stats/tag-network")>
    Public Sub ApiStatsTagNetwork(req As HttpRequest, res As HttpResponse)
        Call writeStatistic(res, NugetStatistics.TagNetworkStatName, Function() NugetStatistics.BuildTagNetwork(store.ReadVisiblePackages()))
    End Sub

    <HttpGet("/api/stats/dependency-network")>
    Public Sub ApiStatsDependencyNetwork(req As HttpRequest, res As HttpResponse)
        Call writeStatistic(res, NugetStatistics.DependencyNetworkStatName, Function() NugetStatistics.BuildDependencyNetwork(store.ReadVisiblePackages()))
    End Sub

    ''' <summary>
    ''' the precomputed umap / kmeans cluster document of the feed. an empty
    ''' document is returned (with a hint message) while the first periodic
    ''' analysis has not finished yet.
    ''' </summary>
    <HttpGet("/api/stats/clusters")>
    Public Sub ApiStatsClusters(req As HttpRequest, res As HttpResponse)
        Dim document As String = store.GetStatistic(PackageClusterAnalysis.ClustersStatName)

        res.AccessControlAllowOrigin = "*"

        If document.StringEmpty() Then
            writeJson(res, New Dictionary(Of String, Object) From {
                {"k", 0},
                {"samples", 0},
                {"tags", 0},
                {"clusters", New List(Of Object)},
                {"points", New List(Of Object)},
                {"message", "the cluster analysis has not been built yet"}
            })
            Return
        End If

        Call writeRawJson(res, document)
    End Sub

    ''' <summary>
    ''' force a cluster analysis rebuild; the uploaded TOTP credentials of an
    ''' ``official`` server administrator account are required. an optional
    ''' ``k`` argument overrides the configured number of clusters.
    ''' </summary>
    <HttpPost("/api/stats/clusters/rebuild")>
    Public Sub ApiStatsClustersRebuild(req As HttpPOSTRequest, res As HttpResponse)
        Dim email As String = argument(req, "email")

        If Not auth.Authenticate(email, argument(req, "code")) Then
            res.WriteError(HTTP_RFC.RFC_UNAUTHORIZED, "invalid email or TOTP code")
            Return
        End If

        If Not store.IsUserOfficial(email) Then
            Call $"cluster rebuild rejected: '{email}' is not an official account".warning()
            res.WriteError(HTTP_RFC.RFC_FORBIDDEN, "only an official server administrator account can rebuild the cluster analysis")
            Return
        End If

        Dim k As Integer = 0
        Dim requested As String = argument(req, "k")

        If Not requested.StringEmpty() Then
            If Not Integer.TryParse(requested, k) OrElse k < 2 OrElse k > 64 Then
                res.WriteError(HTTP_RFC.RFC_BAD_REQUEST, "invalid k value: an integer between 2 and 64 is expected")
                Return
            End If
        End If

        Dim summary As PackageClusterAnalysis.AnalysisSummary = PackageClusterAnalysis.RunIfChanged(store, config, forceK:=k)

        Call writeResult(res, summary.Success, summary.Message, analysisSummaryJson(summary))
    End Sub

    Private Shared Function analysisSummaryJson(summary As PackageClusterAnalysis.AnalysisSummary) As Dictionary(Of String, Object)
        Return New Dictionary(Of String, Object) From {
            {"rebuilt", summary.Rebuilt},
            {"samples", summary.Samples},
            {"tags", summary.Tags},
            {"k", summary.K},
            {"clusters", summary.Clusters},
            {"latestPackages", summary.LatestPackages},
            {"elapsedMilliseconds", summary.ElapsedMilliseconds}
        }
    End Function

    <HttpPost("/api/stats/rebuild")>
    Public Sub ApiStatsRebuild(req As HttpPOSTRequest, res As HttpResponse)
        Dim email As String = argument(req, "email")

        If Not auth.Authenticate(email, argument(req, "code")) Then
            res.WriteError(HTTP_RFC.RFC_UNAUTHORIZED, "invalid email or TOTP code")
            Return
        End If

        If Not store.IsUserOfficial(email) Then
            Call $"stats rebuild rejected: '{email}' is not an official account".warning()
            res.WriteError(HTTP_RFC.RFC_FORBIDDEN, "only an official server administrator account can rebuild the statistics")
            Return
        End If

        Dim summary As Dictionary(Of String, Object) = refreshStatistics()

        Call writeResult(res, True, "statistics rebuilt", summary)
    End Sub

    ''' <summary>
    ''' serve a precomputed statistic document, rebuilding it lazily on the
    ''' first access (for example on a database that predates the statistics
    ''' feature).
    ''' </summary>
    Private Sub writeStatistic(res As HttpResponse, name As String, build As Func(Of String))
        Dim payload As String = store.GetStatistic(name)

        If String.IsNullOrEmpty(payload) Then
            payload = build()

            If Not String.IsNullOrEmpty(payload) Then
                Call store.SaveStatistic(name, payload)
            Else
                payload = "{}"
            End If
        End If

        res.AccessControlAllowOrigin = "*"
        Call writeRawJson(res, payload)
    End Sub

    ''' <summary>
    ''' recompute and persist all of the feed statistics; returns a small
    ''' summary of the produced documents.
    ''' </summary>
    Private Function refreshStatistics() As Dictionary(Of String, Object)
        Dim packages As List(Of PackageRecord) = store.ReadVisiblePackages()

        Dim tags As String = NugetStatistics.BuildTags(packages)
        Dim tagNetwork As String = NugetStatistics.BuildTagNetwork(packages)
        Dim dependencyNetwork As String = NugetStatistics.BuildDependencyNetwork(packages)

        Call store.SaveStatistic(NugetStatistics.TagsStatName, tags)
        Call store.SaveStatistic(NugetStatistics.TagNetworkStatName, tagNetwork)
        Call store.SaveStatistic(NugetStatistics.DependencyNetworkStatName, dependencyNetwork)

        Call $"statistics rebuilt: packages={packages.Count}".info()

        Return New Dictionary(Of String, Object) From {
            {"packages", packages.Count},
            {"documents", New Dictionary(Of String, Object) From {
                {"tags", tags.Length},
                {"tagNetwork", tagNetwork.Length},
                {"dependencyNetwork", dependencyNetwork.Length}
            }},
            {"updated", isoDate(Date.UtcNow)}
        }
    End Function

#End Region

#Region "api document pages (server side rendered, pseudo static)"

    ''' <summary>
    ''' the pseudo static route of the site map: it answers the ``sitemap.xml``
    ''' which was generated in the scratch directory. when the file is missing
    ''' (the server was never idle long enough, or the scratch folder was
    ''' cleared) it is generated on the fly, so that a crawler never sees a 404.
    ''' 
    ''' the reference to the style sheet is served by the static file system.
    ''' </summary>
    <HttpGet("/sitemap.xml")>
    Public Sub SitemapXml(req As HttpRequest, res As HttpResponse)
        Call SitemapScheduler.TouchRequest()

        Dim xml As String = If(sitemap IsNot Nothing, sitemap.TryReadXml(), Nothing)

        If String.IsNullOrEmpty(xml) Then
            res.WriteError(HTTP_RFC.RFC_NOT_FOUND,
                "the sitemap is not available yet; it is generated when the document database changes and the server is idle")
            Return
        End If

        Dim bytes As Byte() = Encoding.UTF8.GetBytes(xml)

        res.AccessControlAllowOrigin = "*"
        res.WriteContent(bytes, "application/xml; charset=utf-8")
    End Sub

    ''' <summary>
    ''' the global cross package api document index page.
    ''' </summary>
    <HttpGet("/docs")>
    Public Sub ApiDocGlobal(req As HttpRequest, res As HttpResponse)
        Call writeDocumentPage(res, Function() ApiDocPages.RenderGlobalIndex(store, config))
    End Sub

    ''' <summary>
    ''' the global cross package api document index page.
    ''' </summary>
    <HttpGet("/docs/index.html")>
    Public Sub ApiDocGlobalIndex(req As HttpRequest, res As HttpResponse)
        Call writeDocumentPage(res, Function() ApiDocPages.RenderGlobalIndex(store, config))
    End Sub

    ''' <summary>
    ''' the per package api document index page (``.../index.html``) and the type
    ''' content page (``.../&lt;type full name&gt;.html``).
    ''' </summary>
    <HttpGet("/docs/{id}/{version}/{name}.html")>
    Public Sub ApiDocPage(req As HttpRequest, res As HttpResponse)
        Dim id As String = routeValue(req, "id")
        Dim version As String = routeValue(req, "version")
        Dim name As String = Uri.UnescapeDataString(routeValue(req, "name"))
        Dim html As String

        ' the api documentation of a hidden package may not be read either
        If store.IsPackageHidden(id) Then
            Call redirectNotFound(res, "docs", id)
            Return
        End If

        If name.Equals("index", StringComparison.OrdinalIgnoreCase) Then
            html = ApiDocPages.RenderPackageIndex(store, config, id, version)
        Else
            html = ApiDocPages.RenderTypePage(store, config, id, version, name)
        End If

        If String.IsNullOrEmpty(html) Then
            Call redirectNotFound(res, "docs", id)
            Return
        End If

        ' count the documentation page view of the current utc day. Only the per
        ' package pages reach this point: the global documentation index is
        ' served by the other routes and is not a part of any single package.
        Call store.RecordDocView(id)

        Call writeHtml(res, html)
    End Sub

    ''' <summary>
    ''' render a document page and answer 404 when the renderer returns nothing.
    ''' </summary>
    ''' <param name="res"></param>
    ''' <param name="render"></param>
    Private Sub writeDocumentPage(res As HttpResponse, render As Func(Of String))
        Dim html As String = render()

        If String.IsNullOrEmpty(html) Then
            Call redirectNotFound(res, "docs")
        Else
            Call writeHtml(res, html)
        End If
    End Sub

    ''' <summary>
    ''' hand the browser over to the static 404 page of the web front end. the
    ''' server side rendered html routes use it instead of a plain text error
    ''' body, so that a visitor who follows an outdated or withdrawn link sees
    ''' the styled page. the json api endpoints keep their plain error response,
    ''' which is what an api client expects.
    ''' </summary>
    ''' <param name="res">the response to write the redirect to.</param>
    ''' <param name="kind">the kind of the missing resource (``package``, ``user`` or ``docs``).</param>
    ''' <param name="id">the requested id, carried over for the page context.</param>
    Private Sub redirectNotFound(res As HttpResponse, kind As String, Optional id As String = Nothing)
        Dim url As String = $"/404.html?type={Uri.EscapeDataString(If(kind, ""))}"

        If Not String.IsNullOrEmpty(id) Then
            url &= $"&id={Uri.EscapeDataString(id)}"
        End If

        res.Redirect(url)
    End Sub

    ''' <summary>
    ''' write an utf-8 html response body.
    ''' </summary>
    ''' <param name="res"></param>
    ''' <param name="html"></param>
    Private Shared Sub writeHtml(res As HttpResponse, html As String)
        Call SitemapScheduler.TouchRequest()

        Dim bytes As Byte() = Encoding.UTF8.GetBytes(html)

        res.WriteContent(bytes, "text/html; charset=utf-8")
    End Sub

#End Region

#Region "helpers"

    Private Function versionDirectory(pkg As PackageRecord) As String
        Return Path.Combine(config.PackageDirectory, pkg.package_id.ToLowerInvariant(), pkg.version.ToLowerInvariant())
    End Function

    Private Function nupkgFilePath(pkg As PackageRecord) As String
        Dim idLower As String = pkg.package_id.ToLowerInvariant()
        Dim versionLower As String = pkg.version.ToLowerInvariant()
        Return Path.Combine(versionDirectory(pkg), $"{idLower}.{versionLower}.nupkg")
    End Function

    Private Function nuspecFilePath(pkg As PackageRecord) As String
        Return Path.Combine(versionDirectory(pkg), $"{pkg.package_id.ToLowerInvariant()}.nuspec")
    End Function

    Private Sub registerStaticFiles(pkg As PackageRecord)
        If router Is Nothing OrElse router.FileSystem Is Nothing Then
            Return
        End If

        Dim idLower As String = pkg.package_id.ToLowerInvariant()
        Dim versionLower As String = pkg.version.ToLowerInvariant()
        Dim fs As Flute.Http.FileSystem.FileSystem = router.FileSystem.fs(0)

        Call fs.AddMapping($"/packages/{idLower}/{versionLower}/{idLower}.{versionLower}.nupkg", nupkgFilePath(pkg))
        Call fs.AddMapping($"/packages/{idLower}/{versionLower}/{idLower}.nuspec", nuspecFilePath(pkg))
    End Sub

    ''' <summary>
    ''' build the queryable tag / dependency index and persist the full nuspec
    ''' metadata of a freshly published package version.
    ''' </summary>
    Private Sub indexPackage(pkg As PackageRecord, metadata As NupkgMetadata)
        Call store.ReplacePackageTags(pkg.package_id, NugetStatistics.SplitTags(pkg.tags))
        Call store.ReplacePackageDependencies(pkg.package_id, pkg.version, metadata.DependencyItems)

        Dim values As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
            {"id", metadata.Id},
            {"version", metadata.Version},
            {"title", metadata.Title},
            {"authors", metadata.Authors},
            {"owners", metadata.Owners},
            {"description", metadata.Description},
            {"summary", metadata.Summary},
            {"releaseNotes", metadata.ReleaseNotes},
            {"copyright", metadata.Copyright},
            {"language", metadata.Language},
            {"tags", metadata.Tags},
            {"projectUrl", metadata.ProjectUrl},
            {"licenseUrl", metadata.LicenseUrl},
            {"license", metadata.License},
            {"requireLicenseAcceptance", metadata.RequireLicenseAcceptance},
            {"repository", metadata.Repository},
            {"icon", metadata.Icon},
            {"nuspec", metadata.RawXml}
        }

        ' extract the embedded icon image so that it can be served on the web page
        If Not String.IsNullOrEmpty(metadata.Icon) Then
            ' the extension is resolved against a whitelist: a hostile nuspec
            ' value can never control the extracted file name
            Dim extension As String = NupkgReader.SafeExtension(metadata.Icon, NupkgReader.IconExtensions, ".png")
            Dim iconPath As String = Path.Combine(versionDirectory(pkg), "icon" & extension)

            If NupkgReader.ExtractIcon(nupkgFilePath(pkg), metadata.Icon, iconPath,
                                       New ZipExtractionLimits(config.ZipMaxEntryMB, config.ZipMaxTotalMB, config.ZipMaxEntries)) Then
                values("iconFile") = "icon" & extension
            End If
        End If

        ' extract the embedded readme document so that the web detail page can
        ' render it later on. the text itself is not stored in the database
        ' because the JSql string literal escaping flattens the line breaks.
        If Not String.IsNullOrEmpty(metadata.Readme) Then
            Dim readmeExtension As String = NupkgReader.SafeExtension(metadata.Readme, NupkgReader.ReadmeExtensions, ".md")
            Dim readmeFile As String = "readme" & readmeExtension.ToLowerInvariant()
            Dim readmePath As String = Path.Combine(versionDirectory(pkg), readmeFile)

            If NupkgReader.ExtractEntry(nupkgFilePath(pkg), metadata.Readme, readmePath,
                                        New ZipExtractionLimits(config.ZipMaxEntryMB, config.ZipMaxTotalMB, config.ZipMaxEntries)) Then
                values("readme") = metadata.Readme
                values("readmeFile") = readmeFile
                values("readmeFormat") = readmeExtension.TrimStart("."c).ToLowerInvariant()
                Call $"readme extracted: {pkg.package_id} {pkg.version} -> {readmeFile}".debug()
            Else
                Call $"the readme document '{metadata.Readme}' was not found in {pkg.package_id} {pkg.version}".warning()
            End If
        End If

        Call store.ReplacePackageMetadata(pkg.package_id, pkg.version, values)
    End Sub

    ''' <summary>
    ''' extract the api comment documents of a freshly published package and
    ''' persist them into the document table. The extraction is best effort: any
    ''' failure is only logged as a warning, so that a package could still be
    ''' published even when its documentation could not be parsed.
    ''' </summary>
    ''' <param name="pkg">the published package version.</param>
    Private Sub indexApiDocs(pkg As PackageRecord)
        ' the package itself was already added to the feed, so the site map is
        ' stale from this point on, even when the document extraction fails
        ' below. a racing generation is harmless: the fingerprint of the
        ' database is compared as well, so the next idle window rebuilds it.
        If sitemap IsNot Nothing Then
            Call sitemap.NotifyDocsChanged()
        End If

        Try
            Dim warnings As New List(Of String)
            Dim records As List(Of PackageApiDocRecord) = PackageApiDocs.Extract(
                nupkgFilePath(pkg), pkg.package_id, pkg.version, warnings, config.ApiDocWorkerTimeoutSeconds)

            Call store.ReplacePackageApiDocs(pkg.package_id, pkg.version, records)

            For Each message As String In warnings
                Call $"api document {pkg.package_id} {pkg.version}: {message}".warning()
            Next

            If records.Count > 0 Then
                Call $"api documents extracted: {pkg.package_id} {pkg.version} -> {records.Count} type(s)".debug()
            End If
        Catch ex As Exception
            Call $"api document extraction failed for {pkg.package_id} {pkg.version}: {ex.Message}".warning()
            Call App.LogException(ex)
        End Try
    End Sub

    Private Function getBaseUrl(req As HttpRequest) As String
        If config IsNot Nothing AndAlso Not String.IsNullOrEmpty(config.BaseUrl) Then
            Return config.BaseUrl.TrimEnd("/"c)
        End If

        Dim host As String = req.HttpHeaders.TryGetValue("Host")
        If String.IsNullOrEmpty(host) Then
            host = "localhost"
        End If

        Return "http://" & host
    End Function

    Private Shared Function resource(id As String, type As String, comment As String) As Dictionary(Of String, Object)
        Return New Dictionary(Of String, Object) From {
            {"@id", id},
            {"@type", type},
            {"comment", comment}
        }
    End Function

    Private Shared Function routeValue(req As HttpRequest, name As String) As String
        If req.RouteData IsNot Nothing Then
            Dim value As String = Nothing
            If req.RouteData.TryGetValue(name, value) Then
                Return value
            End If
        End If
        Return Nothing
    End Function

    Private Shared Function argument(req As HttpRequest, name As String) As String
        Dim post As HttpPOSTRequest = TryCast(req, HttpPOSTRequest)

        If post IsNot Nothing AndAlso post.POSTData IsNot Nothing Then
            Dim value As String = post.POSTData.Form(name)
            If Not String.IsNullOrEmpty(value) Then
                Return value.Trim()
            End If

            Dim [object] As Object = Nothing
            If post.POSTData.Objects IsNot Nothing AndAlso
               post.POSTData.Objects.TryGetValue(name, [object]) AndAlso [object] IsNot Nothing Then
                Return [object].ToString().Trim()
            End If
        End If

        Return queryValue(req, name)
    End Function

    Private Shared Function queryValue(req As HttpRequest, name As String) As String
        Dim table As Dictionary(Of String, String()) = req.URL.query

        If table IsNot Nothing Then
            Dim values As String() = Nothing
            If table.TryGetValue(name.ToLowerInvariant(), values) AndAlso
               values IsNot Nothing AndAlso values.Length > 0 Then
                Return If(values(0), "").Trim()
            End If
        End If

        Return ""
    End Function

    Private Shared Function queryInt(req As HttpRequest, name As String, fallback As Integer) As Integer
        Dim value As Integer
        If Integer.TryParse(queryValue(req, name), value) AndAlso value >= 0 Then
            Return value
        End If
        Return fallback
    End Function

    Private Shared Function splitTags(text As String) As String()
        If String.IsNullOrEmpty(text) Then
            Return New String() {}
        End If
        Return text.Split(New Char() {" "c, ","c, ";"c}, StringSplitOptions.RemoveEmptyEntries)
    End Function

    Private Shared Function isoDate(value As Date) As String
        If value = Date.MinValue Then
            value = Date.UtcNow
        End If
        Return value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)
    End Function

    Private Shared Function computeSha256(path As String) As String
        Using stream As Stream = File.OpenRead(path)
            Using algorithm As SHA256 = SHA256.Create()
                Dim hash As Byte() = algorithm.ComputeHash(stream)
                Dim sb As New StringBuilder

                For Each b As Byte In hash
                    sb.Append(b.ToString("x2"))
                Next

                Return sb.ToString()
            End Using
        End Using
    End Function

    Private Shared Function firstUpload(req As HttpPOSTRequest) As HttpPostedFile
        If req.POSTData Is Nothing OrElse req.POSTData.files Is Nothing Then
            Return Nothing
        End If

        Dim list As List(Of HttpPostedFile) = Nothing
        If req.POSTData.files.TryGetValue("file", list) AndAlso list IsNot Nothing AndAlso list.Count > 0 Then
            Return list(0)
        End If

        For Each item In req.POSTData.files
            If item.Value IsNot Nothing AndAlso item.Value.Count > 0 Then
                Return item.Value(0)
            End If
        Next

        Return Nothing
    End Function

    Private Shared ReadOnly JsonOptions As New JsonSerializerOptions With {
        .PropertyNameCaseInsensitive = True
    }

    ''' <summary>
    ''' write the given object as a json response body. the system text json
    ''' serializer is used instead of the built in <c>WriteJSON</c> because the
    ''' nuget protocol documents are built from heterogeneous dictionaries.
    ''' </summary>
    Private Shared Sub writeJson(res As HttpResponse, payload As Object)
        Call writeRawJson(res, JsonSerializer.Serialize(payload, JsonOptions))
    End Sub

    ''' <summary>
    ''' write a precomputed json document directly to the response body without
    ''' any additional serialization.
    ''' </summary>
    Private Shared Sub writeRawJson(res As HttpResponse, json As String)
        ' report the request activity to the site map schedule; the router of
        ' the host has no request middleware which a module could hook, so the
        ' response helpers of this controller feed the idle clock.
        Call SitemapScheduler.TouchRequest()

        Dim bytes As Byte() = Encoding.UTF8.GetBytes(json)

        res.WriteContent(bytes, "application/json")
    End Sub

    Private Shared Sub writeResult(res As HttpResponse, ok As Boolean, message As String, Optional data As Dictionary(Of String, Object) = Nothing)

        Dim payload As New Dictionary(Of String, Object) From {
            {"ok", ok},
            {"message", message}
        }

        If data IsNot Nothing Then
            For Each item In data
                payload(item.Key) = item.Value
            Next
        End If

        res.AccessControlAllowOrigin = "*"
        writeJson(res, payload)
    End Sub

    Private Class DependencyInfo
        Public Property id As String
        Public Property range As String
    End Class

    Private Shared Function parseDependencies(text As String) As List(Of DependencyInfo)
        Dim list As New List(Of DependencyInfo)

        If String.IsNullOrEmpty(text) Then
            Return list
        End If

        For Each part As String In text.Split(";"c)
            If String.IsNullOrEmpty(part) Then
                Continue For
            End If

            Dim index As Integer = part.IndexOf("|"c)
            If index < 0 Then
                list.Add(New DependencyInfo With {.id = part, .range = ""})
            Else
                list.Add(New DependencyInfo With {
                    .id = part.Substring(0, index),
                    .range = part.Substring(index + 1)
                })
            End If
        Next

        Return list
    End Function

#End Region
End Class
