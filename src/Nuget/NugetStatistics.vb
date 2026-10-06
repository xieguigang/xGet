Imports System.Collections.Generic
Imports System.Linq
Imports System.Text.Json

''' <summary>
''' precomputes the package statistics of the feed: the tag distribution, the
''' package relation network based on the shared tags and the dependency
''' network. every statistic is produced as a json document so that it can be
''' stored inside the JSql ``statistics`` table and served to the web front end
''' without any real time recomputation.
''' </summary>
''' <remarks>
''' all statistics are computed per distinct package id (the latest version),
''' so that multiple versions of the same package do not amplify the counts.
''' </remarks>
Public Module NugetStatistics

    ''' <summary>the statistics key of the tag distribution document.</summary>
    Public Const TagsStatName As String = "tags"

    ''' <summary>the statistics key of the package relation network document.</summary>
    Public Const TagNetworkStatName As String = "tag-network"

    ''' <summary>the statistics key of the dependency network document.</summary>
    Public Const DependencyNetworkStatName As String = "dependency-network"

    Private ReadOnly JsonOptions As New JsonSerializerOptions With {
        .PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        .WriteIndented = False
    }

    '==================== tag distribution ====================

    ''' <summary>
    ''' build the tag frequency distribution document:
    ''' <c>{ totalPackages, tags: [ { name, count } ] }</c>.
    ''' </summary>
    Public Function BuildTags(packages As List(Of PackageRecord)) As String
        Dim latest As List(Of PackageRecord) = LatestPackages(packages)
        Dim counter As New Dictionary(Of String, Integer)(StringComparer.OrdinalIgnoreCase)

        For Each pkg As PackageRecord In latest
            For Each tag As String In SplitTags(pkg.tags).Distinct(StringComparer.OrdinalIgnoreCase)
                Dim count As Integer = 0
                Call counter.TryGetValue(tag, count)
                counter(tag) = count + 1
            Next
        Next

        Dim rows As List(Of Object) = counter _
            .OrderByDescending(Function(item) item.Value) _
            .ThenBy(Function(item) item.Key, StringComparer.OrdinalIgnoreCase) _
            .Select(Function(item) CObj(New Dictionary(Of String, Object) From {
                {"name", item.Key},
                {"count", item.Value}
            })) _
            .ToList()

        Return JsonSerializer.Serialize(New Dictionary(Of String, Object) From {
            {"totalPackages", latest.Count},
            {"totalTags", counter.Count},
            {"tags", rows}
        }, JsonOptions)
    End Function

    '==================== package relation network ====================

    ''' <summary>
    ''' build the package relation network where two packages are connected when
    ''' they share at least one tag; the edge weight is the number of shared
    ''' tags:
    ''' <c>{ nodes: [ { id, name, tags, value } ], links: [ { source, target, weight } ] }</c>.
    ''' </summary>
    Public Function BuildTagNetwork(packages As List(Of PackageRecord)) As String
        Dim latest As List(Of PackageRecord) = LatestPackages(packages)

        Dim tagSets As New Dictionary(Of String, HashSet(Of String))(StringComparer.OrdinalIgnoreCase)
        Dim display As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)

        For Each pkg As PackageRecord In latest
            Dim idLower As String = pkg.package_id.ToLowerInvariant()
            Dim tags As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

            For Each tag As String In SplitTags(pkg.tags)
                Call tags.Add(tag)
            Next

            tagSets(idLower) = tags
            display(idLower) = pkg.package_id
        Next

        ' inverted index: tag -> the packages that carry it
        Dim inverted As New Dictionary(Of String, List(Of String))(StringComparer.OrdinalIgnoreCase)

        For Each item In tagSets
            For Each tag As String In item.Value
                Dim bucket As List(Of String) = Nothing
                If Not inverted.TryGetValue(tag, bucket) Then
                    bucket = New List(Of String)
                    inverted(tag) = bucket
                End If
                Call bucket.Add(item.Key)
            Next
        Next

        ' a tag that is shared by the majority of the packages (for example the
        ' umbrella ``scibasic`` tag) would connect everything into a single dense
        ' clique, so it is treated as a generic tag and ignored by the relation
        ' network to keep the graph readable.
        Dim genericLimit As Integer = Math.Max(2, CInt(Math.Ceiling(tagSets.Count * 0.5)))

        ' for every (non generic) tag, accumulate the shared counter of each pair
        Dim weights As New Dictionary(Of String, Integer)(StringComparer.Ordinal)

        For Each item In inverted
            Dim ids As List(Of String) = item.Value

            If ids.Count > genericLimit Then
                Continue For
            End If

            For i As Integer = 0 To ids.Count - 2
                For j As Integer = i + 1 To ids.Count - 1
                    Dim a As String = ids(i)
                    Dim b As String = ids(j)

                    If String.CompareOrdinal(a, b) > 0 Then
                        Dim swap As String = a
                        a = b
                        b = swap
                    End If

                    Dim key As String = a & vbTab & b
                    Dim count As Integer = 0
                    Call weights.TryGetValue(key, count)
                    weights(key) = count + 1
                Next
            Next
        Next

        ' only keep the packages that take part in at least one relation
        Dim connected As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

        For Each item In weights
            Dim parts As String() = item.Key.Split(vbTab(0))
            Call connected.Add(parts(0))
            Call connected.Add(parts(1))
        Next

        Dim nodes As List(Of Object) = tagSets _
            .Where(Function(item) connected.Contains(item.Key)) _
            .Select(Function(item) CObj(New Dictionary(Of String, Object) From {
                {"id", item.Key},
                {"name", display(item.Key)},
                {"tags", item.Value.ToArray()},
                {"value", item.Value.Count}
            })) _
            .ToList()

        Dim links As New List(Of Object)

        For Each item In weights
            Dim parts As String() = item.Key.Split(vbTab(0))
            links.Add(New Dictionary(Of String, Object) From {
                {"source", parts(0)},
                {"target", parts(1)},
                {"weight", item.Value}
            })
        Next

        Return JsonSerializer.Serialize(New Dictionary(Of String, Object) From {
            {"nodes", nodes},
            {"links", links}
        }, JsonOptions)
    End Function

    '==================== dependency network ====================

    ''' <summary>
    ''' build the dependency network where each edge points from the depending
    ''' package to the package it depends on. dependencies that are not hosted
    ''' in this feed are added as <c>external</c> nodes, and every hosted node
    ''' carries the host of its nuspec ``projectUrl`` (an empty string when the
    ''' package declares no project url) for the project colour grouping:
    ''' <c>{ nodes: [ { id, name, external, project } ], links: [ { source, target } ] }</c>.
    ''' </summary>
    Public Function BuildDependencyNetwork(packages As List(Of PackageRecord)) As String
        Dim latest As List(Of PackageRecord) = LatestPackages(packages)
        Dim nodes As New Dictionary(Of String, Dictionary(Of String, Object))(StringComparer.OrdinalIgnoreCase)

        For Each pkg As PackageRecord In latest
            Dim idLower As String = pkg.package_id.ToLowerInvariant()
            nodes(idLower) = New Dictionary(Of String, Object) From {
                {"id", idLower},
                {"name", pkg.package_id},
                {"external", False},
                {"project", NugetStore.ProjectHost(pkg.project_url)}
            }
        Next

        Dim links As New List(Of Object)
        Dim seen As New HashSet(Of String)(StringComparer.Ordinal)

        For Each pkg As PackageRecord In latest
            Dim fromId As String = pkg.package_id.ToLowerInvariant()

            For Each dependencyId As String In ParseDependencyIds(pkg.dependencies)
                Dim toId As String = dependencyId.ToLowerInvariant()

                If Not nodes.ContainsKey(toId) Then
                    nodes(toId) = New Dictionary(Of String, Object) From {
                        {"id", toId},
                        {"name", dependencyId},
                        {"external", True},
                        {"project", ""}
                    }
                End If

                Dim key As String = fromId & vbTab & toId
                If seen.Add(key) Then
                    links.Add(New Dictionary(Of String, Object) From {
                        {"source", fromId},
                        {"target", toId}
                    })
                End If
            Next
        Next

        Return JsonSerializer.Serialize(New Dictionary(Of String, Object) From {
            {"nodes", nodes.Values.ToList()},
            {"links", links}
        }, JsonOptions)
    End Function

    '==================== helpers ====================

    ''' <summary>
    ''' pick the latest version of each distinct package id, so that the
    ''' statistics are computed per package instead of per version.
    ''' </summary>
    Public Function LatestPackages(packages As List(Of PackageRecord)) As List(Of PackageRecord)
        Dim list As New List(Of PackageRecord)

        If packages Is Nothing Then
            Return list
        End If

        Dim groups = packages _
            .Where(Function(p) p.package_id IsNot Nothing AndAlso Not p.package_id.StringEmpty()) _
            .GroupBy(Function(p) p.package_id.ToLowerInvariant())

        For Each group In groups
            list.Add(group.OrderBy(Function(p) NugetStore.VersionKey(p.version)).Last())
        Next

        Return list
    End Function

    ''' <summary>
    ''' split the raw tag string into individual tags.
    ''' </summary>
    Public Function SplitTags(text As String) As String()
        If String.IsNullOrEmpty(text) Then
            Return New String() {}
        End If
        Return text.Split(New Char() {" "c, ","c, ";"c}, StringSplitOptions.RemoveEmptyEntries)
    End Function

    ''' <summary>
    ''' extract the dependency package ids from the encoded dependency string
    ''' (``id|range;id|range``).
    ''' </summary>
    Public Function ParseDependencyIds(text As String) As List(Of String)
        Dim list As New List(Of String)

        If String.IsNullOrEmpty(text) Then
            Return list
        End If

        For Each part As String In text.Split(";"c)
            If part.StringEmpty() Then
                Continue For
            End If

            Dim index As Integer = part.IndexOf("|"c)
            Dim id As String = If(index < 0, part, part.Substring(0, index)).Trim()

            If Not id.StringEmpty() Then
                list.Add(id)
            End If
        Next

        Return list
    End Function
End Module
