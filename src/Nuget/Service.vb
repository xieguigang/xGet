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

    Public Sub Mount(router As HttpRouter, config As IReadOnlyDictionary(Of String, String)) Implements IHttpAppModule.Mount
        Me.router = router
        Me.config = NugetConfiguration.FromConfig(config)

        Call Directory.CreateDirectory(Me.config.DataDirectory)
        Call Directory.CreateDirectory(Me.config.PackageDirectory)
        Call Directory.CreateDirectory(Me.config.DatabaseDirectory)

        Me.store = New NugetStore(Me.config.DatabaseDirectory, Me.config.CreateStorageOptions())
        Me.auth = New TotpAuth(Me.store)

        Call $"nuget server data directory: {Me.config.DataDirectory}".info()

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
        Dim email As String = argument(req, "email")

        If String.IsNullOrEmpty(email) Then
            res.WriteError(HTTP_RFC.RFC_BAD_REQUEST, "the email argument is required")
            Return
        End If

        Dim user As UserRecord = auth.Register(email)

        If user Is Nothing Then
            res.WriteError(HTTP_RFC.RFC_INTERNAL_SERVER_ERROR, "failed to register the user")
            Return
        End If

        Call writeResult(res, True, $"registered {user.email}", New Dictionary(Of String, Object) From {
            {"email", user.email},
            {"secret", user.secretKey},
            {"issuer", "nuget"},
            {"otpauth", TotpModule.BuildOtpAuthUri(user.secretKey, user.email, "nuget")}
        })
    End Sub

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

        Dim upload As HttpPostedFile = firstUpload(req)

        If upload Is Nothing Then
            res.WriteError(HTTP_RFC.RFC_BAD_REQUEST, "the package file is required (multipart field 'file')")
            Return
        End If

        Dim temp As String = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") & ".nupkg")

        Try
            Call upload.SaveAs(temp)

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

            If store.PackageExists(metadata.Id, metadata.Version) Then
                res.WriteError(HTTP_RFC.RFC_CONFLICT, $"package {metadata.Id} {metadata.Version} already exists")
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
        For Each pkg As PackageRecord In store.ReadAllPackages().OrderByDescending(Function(p) p.published).Take(10)
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
        res.WriteHeader(mime, bytes.Length)
        Call res.SendData(bytes)
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
            {"published", isoDate(pkg.published)}
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

#Region "statistics"

    <HttpGet("/api/stats/tags")>
    Public Sub ApiStatsTags(req As HttpRequest, res As HttpResponse)
        Call writeStatistic(res, NugetStatistics.TagsStatName, Function() NugetStatistics.BuildTags(store.ReadAllPackages()))
    End Sub

    <HttpGet("/api/stats/tag-network")>
    Public Sub ApiStatsTagNetwork(req As HttpRequest, res As HttpResponse)
        Call writeStatistic(res, NugetStatistics.TagNetworkStatName, Function() NugetStatistics.BuildTagNetwork(store.ReadAllPackages()))
    End Sub

    <HttpGet("/api/stats/dependency-network")>
    Public Sub ApiStatsDependencyNetwork(req As HttpRequest, res As HttpResponse)
        Call writeStatistic(res, NugetStatistics.DependencyNetworkStatName, Function() NugetStatistics.BuildDependencyNetwork(store.ReadAllPackages()))
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
    ''' force a cluster analysis rebuild; the uploaded TOTP credentials of a
    ''' registered user are required. an optional ``k`` argument overrides the
    ''' configured number of clusters.
    ''' </summary>
    <HttpPost("/api/stats/clusters/rebuild")>
    Public Sub ApiStatsClustersRebuild(req As HttpPOSTRequest, res As HttpResponse)
        If Not auth.Authenticate(argument(req, "email"), argument(req, "code")) Then
            res.WriteError(HTTP_RFC.RFC_UNAUTHORIZED, "invalid email or TOTP code")
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
        If Not auth.Authenticate(argument(req, "email"), argument(req, "code")) Then
            res.WriteError(HTTP_RFC.RFC_UNAUTHORIZED, "invalid email or TOTP code")
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
        Dim packages As List(Of PackageRecord) = store.ReadAllPackages()

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

        If name.Equals("index", StringComparison.OrdinalIgnoreCase) Then
            html = ApiDocPages.RenderPackageIndex(store, config, id, version)
        Else
            html = ApiDocPages.RenderTypePage(store, config, id, version, name)
        End If

        If String.IsNullOrEmpty(html) Then
            res.WriteError(HTTP_RFC.RFC_NOT_FOUND, $"the api document was not found: {id} {version} {name}")
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
            res.WriteError(HTTP_RFC.RFC_NOT_FOUND, "the requested api document page was not found")
        Else
            Call writeHtml(res, html)
        End If
    End Sub

    ''' <summary>
    ''' write an utf-8 html response body.
    ''' </summary>
    ''' <param name="res"></param>
    ''' <param name="html"></param>
    Private Shared Sub writeHtml(res As HttpResponse, html As String)
        Dim bytes As Byte() = Encoding.UTF8.GetBytes(html)

        res.WriteHeader("text/html; charset=utf-8", bytes.Length)
        Call res.SendData(bytes)
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
            Dim extension As String = Path.GetExtension(metadata.Icon)
            If String.IsNullOrEmpty(extension) Then
                extension = ".png"
            End If

            Dim iconPath As String = Path.Combine(versionDirectory(pkg), "icon" & extension)
            If NupkgReader.ExtractIcon(nupkgFilePath(pkg), metadata.Icon, iconPath) Then
                values("iconFile") = "icon" & extension
            End If
        End If

        ' extract the embedded readme document so that the web detail page can
        ' render it later on. the text itself is not stored in the database
        ' because the JSql string literal escaping flattens the line breaks.
        If Not String.IsNullOrEmpty(metadata.Readme) Then
            Dim readmeExtension As String = Path.GetExtension(metadata.Readme)
            If String.IsNullOrEmpty(readmeExtension) Then
                readmeExtension = ".md"
            End If

            Dim readmeFile As String = "readme" & readmeExtension.ToLowerInvariant()
            Dim readmePath As String = Path.Combine(versionDirectory(pkg), readmeFile)

            If NupkgReader.ExtractEntry(nupkgFilePath(pkg), metadata.Readme, readmePath) Then
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
        Try
            Dim warnings As New List(Of String)
            Dim records As List(Of PackageApiDocRecord) = PackageApiDocs.Extract(
                nupkgFilePath(pkg), pkg.package_id, pkg.version, warnings)

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
        Dim bytes As Byte() = Encoding.UTF8.GetBytes(json)

        res.WriteHeader("application/json", bytes.Length)
        Call res.SendData(bytes)
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
