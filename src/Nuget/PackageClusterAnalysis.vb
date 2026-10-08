Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.Globalization
Imports System.Linq
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports Microsoft.VisualBasic.DataMining.ComponentModel.EntityModels
Imports Microsoft.VisualBasic.DataMining.KMeans
Imports Microsoft.VisualBasic.DataMining.UMAP

''' <summary>
''' the periodic tag space analysis of the feed: the packages are described by
''' a 0/1 tag matrix (packages as rows, tags as columns), the matrix is embedded
''' into 3 dimensions with umap and finally clustered with kmeans so that every
''' package gets a three dimensional coordinate and a classification label.
''' </summary>
''' <remarks>
''' the analysis is intentionally driven by a coarse feed fingerprint: the
''' matrix is only rebuilt when a package was added (or its version / tags have
''' changed) since the last run. all of the expensive work happens on the
''' background timer thread of the http server, the web front end only reads the
''' precomputed document out of the JSql ``statistics`` table.
''' </remarks>
Public Module PackageClusterAnalysis

    ''' <summary>the statistics key of the precomputed cluster document.</summary>
    Public Const ClustersStatName As String = "package-clusters"

    ''' <summary>the statistics key of the feed fingerprint of the last run.</summary>
    Public Const ClusterStateStatName As String = "cluster-state"

    ''' <summary>the number of dimensions of the umap embedding.</summary>
    Public Const EmbeddingDimensions As Integer = 3

    Private ReadOnly JsonOptions As New JsonSerializerOptions With {
        .PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        .WriteIndented = False
    }

    ''' <summary>
    ''' serializes the timer callback with the manual rebuild endpoint so that
    ''' two analyses can never run at the same time.
    ''' </summary>
    Private ReadOnly analysisSync As New Object

#Region "models"

    ''' <summary>
    ''' the outcome of one analysis run, also returned to the admin endpoint.
    ''' </summary>
    Public Class AnalysisSummary
        Public Property Success As Boolean

        ''' <summary>whether the analysis was actually recomputed.</summary>
        Public Property Rebuilt As Boolean

        Public Property Message As String
        Public Property Samples As Integer
        Public Property Tags As Integer
        Public Property K As Integer
        Public Property Clusters As Integer
        Public Property LatestPackages As Integer
        Public Property ElapsedMilliseconds As Long
    End Class

    ''' <summary>
    ''' the 0/1 tag matrix of the feed: one row per tagged package and one
    ''' column per distinct tag.
    ''' </summary>
    Public Class TagMatrix
        ''' <summary>the lower-case package ids (the matrix rows).</summary>
        Public Property Ids As New List(Of String)

        ''' <summary>the original package id spelling shown on the web page.</summary>
        Public Property Displays As New List(Of String)

        ''' <summary>the lower-case tag names (the matrix columns).</summary>
        Public Property Tags As New List(Of String)

        ''' <summary>the row × column 0/1 matrix.</summary>
        Public Property Data As Double()()

        ''' <summary>the tag set of every row, keyed by the lower-case package id.</summary>
        Public Property TagSets As New Dictionary(Of String, HashSet(Of String))(StringComparer.OrdinalIgnoreCase)
    End Class

#End Region

    ''' <summary>
    ''' the periodic entry point: rebuild the cluster assignment only when the
    ''' feed has changed since the last successful run, or when a forced k is
    ''' given by the administrator.
    ''' </summary>
    ''' <param name="store">the database access layer.</param>
    ''' <param name="config">the runtime configuration.</param>
    ''' <param name="forceK">a k which overrides the configured value; 0 to honor the configuration.</param>
    Public Function RunIfChanged(store As NugetStore, config As NugetConfiguration, Optional forceK As Integer = 0) As AnalysisSummary
        SyncLock analysisSync
            ' only the publicly visible packages take part in the analysis, so a
            ' hidden package can not be reached through the cluster graph either
            Dim latest As List(Of PackageRecord) = NugetStatistics.LatestPackages(store.ReadVisiblePackages())
            Dim fingerprint As String = ComputeFingerprint(latest)
            Dim k As Integer = If(forceK > 0, forceK, config.ClusterK)

            If forceK <= 0 Then
                Dim state As String = store.GetStatistic(ClusterStateStatName)

                If Not state.StringEmpty() AndAlso
                   String.Equals(stateValue(state, "fingerprint"), fingerprint, StringComparison.Ordinal) AndAlso
                   stateInteger(state, "k") = k Then

                    Return New AnalysisSummary With {
                        .Success = True,
                        .Rebuilt = False,
                        .Message = "the feed has not changed since the last cluster analysis",
                        .Samples = stateInteger(state, "samples"),
                        .Tags = stateInteger(state, "tags"),
                        .K = k,
                        .Clusters = stateInteger(state, "clusters"),
                        .LatestPackages = latest.Count
                    }
                End If
            End If

            Return RunAnalysis(store, latest, fingerprint, k, config)
        End SyncLock
    End Function

    ''' <summary>
    ''' build the tag matrix, embed it into 3 dimensions with umap, cluster the
    ''' embedding with kmeans and persist the result.
    ''' </summary>
    Private Function RunAnalysis(store As NugetStore, latest As List(Of PackageRecord), fingerprint As String,
                                 k As Integer, config As NugetConfiguration) As AnalysisSummary

        Dim timer As Stopwatch = Stopwatch.StartNew()
        Dim matrix As TagMatrix = BuildMatrix(latest)

        If matrix.Ids.Count < config.ClusterMinSamples Then
            Dim message As String = $"skipped the cluster analysis: not enough tagged packages (tagged={matrix.Ids.Count}, required={config.ClusterMinSamples})"
            Call message.warning()

            Return New AnalysisSummary With {
                .Success = False,
                .Rebuilt = False,
                .Message = message,
                .Samples = matrix.Ids.Count,
                .Tags = matrix.Tags.Count,
                .K = k,
                .LatestPackages = latest.Count
            }
        End If

        Try
            ' ---- 1) umap: embed the 0/1 tag matrix into a 3d manifold ----
            Dim neighbors As Integer = Math.Max(2, Math.Min(config.ClusterNeighbors, matrix.Ids.Count - 1))
            Dim umap As New Umap(
                distance:=DistanceFunctions.GetFunction(DistanceFunction.Cosine),
                dimensions:=EmbeddingDimensions,
                numberOfNeighbors:=neighbors,
                minDist:=0.3,
                spread:=1.0)

            Dim epochs As Integer = umap.InitializeFit(matrix.Data)
            Call umap.Step(epochs, tqdm_wrap:=False)

            ' umap returns an arbitrarily translated and scaled manifold; the
            ' embedding is centered so that the coordinates have a stable
            ' numerical range for the database and the web front end (the
            ' relative structure of the embedding is not affected).
            Dim embedding As Double()() = center(umap.GetEmbedding())

            ' ---- 2) kmeans: classify the embedding ----
            ' the kmeans implementation rejects k >= the number of samples, and
            ' a single cluster is not a classification at all.
            Dim clusterCount As Integer = Math.Max(2, Math.Min(k, matrix.Ids.Count - 1))

            Dim points As ClusterEntity() = matrix.Ids _
                .Select(Function(id, i) New ClusterEntity(id, resize(embedding(i), EmbeddingDimensions))) _
                .ToArray

            Dim algorithm As New KMeansAlgorithm(Of ClusterEntity)(
                debug:=False,
                max_iters:=100,
                n_threads:=Math.Max(1, Environment.ProcessorCount))

            Dim clusters As ClusterCollection(Of ClusterEntity) = algorithm.ClusterDataSet(points, clusterCount)

            ' ---- 3) collect the labels / the per cluster size ----
            Dim sizes As New List(Of Integer)
            Dim label As Integer = 1

            For Each cluster As KMeansCluster(Of ClusterEntity) In clusters
                For Each point As ClusterEntity In cluster
                    point.cluster = label
                Next

                Call sizes.Add(cluster.NumOfEntity)
                label += 1
            Next

            Dim updated As Date = Date.UtcNow
            Dim records As New List(Of PackageClusterRecord)
            Dim pointRows As New List(Of Object)

            For i As Integer = 0 To matrix.Ids.Count - 1
                Dim entity As ClusterEntity = points(i)
                Dim coordinate As Double() = entity.entityVector
                Dim id As String = matrix.Ids(i)

                Call records.Add(New PackageClusterRecord With {
                    .package_id = id,
                    .x = coordinate(0),
                    .y = coordinate(1),
                    .z = coordinate(2),
                    .cluster = entity.cluster,
                    .updated = updated
                })

                pointRows.Add(New Dictionary(Of String, Object) From {
                    {"id", id},
                    {"name", matrix.Displays(i)},
                    {"x", coordinate(0)},
                    {"y", coordinate(1)},
                    {"z", coordinate(2)},
                    {"cluster", entity.cluster},
                    {"tags", matrix.TagSets(id).OrderBy(Function(t) t, StringComparer.OrdinalIgnoreCase).ToArray()}
                })
            Next

            Dim clusterRows As New List(Of Object)

            For i As Integer = 0 To sizes.Count - 1
                clusterRows.Add(New Dictionary(Of String, Object) From {
                    {"label", i + 1},
                    {"size", sizes(i)}
                })
            Next

            Dim stamp As String = updated.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)

            Dim document As String = JsonSerializer.Serialize(New Dictionary(Of String, Object) From {
                {"k", clusterCount},
                {"samples", matrix.Ids.Count},
                {"tags", matrix.Tags.Count},
                {"neighbors", neighbors},
                {"updated", stamp},
                {"clusters", clusterRows},
                {"points", pointRows}
            }, JsonOptions)

            Dim state As String = JsonSerializer.Serialize(New Dictionary(Of String, Object) From {
                {"fingerprint", fingerprint},
                {"packages", latest.Count},
                {"samples", matrix.Ids.Count},
                {"tags", matrix.Tags.Count},
                {"k", clusterCount},
                {"clusters", sizes.Count},
                {"updated", stamp}
            }, JsonOptions)

            ' the database is only touched after the whole analysis succeeded, so
            ' a failed run always keeps the previous result available.
            Call store.ReplacePackageClusters(records)
            Call store.SaveStatistic(ClustersStatName, document)
            Call store.SaveStatistic(ClusterStateStatName, state)

            timer.Stop()

            Call $"package cluster analysis finished: packages={latest.Count}, samples={matrix.Ids.Count}, tags={matrix.Tags.Count}, k={clusterCount}, clusters={sizes.Count}, elapsed={timer.ElapsedMilliseconds}ms".info()

            Return New AnalysisSummary With {
                .Success = True,
                .Rebuilt = True,
                .Message = $"clustered {matrix.Ids.Count} packages into {sizes.Count} clusters (k={clusterCount})",
                .Samples = matrix.Ids.Count,
                .Tags = matrix.Tags.Count,
                .K = clusterCount,
                .Clusters = sizes.Count,
                .LatestPackages = latest.Count,
                .ElapsedMilliseconds = timer.ElapsedMilliseconds
            }
        Catch ex As Exception
            timer.Stop()
            Call App.LogException(ex)

            Return New AnalysisSummary With {
                .Success = False,
                .Rebuilt = False,
                .Message = ex.Message,
                .Samples = matrix.Ids.Count,
                .Tags = matrix.Tags.Count,
                .K = k,
                .LatestPackages = latest.Count,
                .ElapsedMilliseconds = timer.ElapsedMilliseconds
            }
        End Try
    End Function

#Region "matrix and fingerprint"

    ''' <summary>
    ''' build the 0/1 tag matrix of the given (latest version only) packages.
    ''' packages without any tag cannot be described by the tag space and are
    ''' skipped, exactly like the user requested.
    ''' </summary>
    ''' <param name="latest">the latest version of every package.</param>
    ''' <returns>the tag matrix.</returns>
    Public Function BuildMatrix(latest As List(Of PackageRecord)) As TagMatrix
        Dim matrix As New TagMatrix

        If latest Is Nothing OrElse latest.Count = 0 Then
            matrix.Data = New Double()() {}
            Return matrix
        End If

        Dim frequency As New Dictionary(Of String, Integer)(StringComparer.OrdinalIgnoreCase)
        Dim sets As New List(Of HashSet(Of String))

        For Each pkg As PackageRecord In latest
            Dim tags As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

            For Each tag As String In NugetStatistics.SplitTags(pkg.tags)
                Dim key As String = tag.Trim().ToLowerInvariant()

                If Not key.StringEmpty() Then
                    Call tags.Add(key)
                End If
            Next

            If tags.Count = 0 Then
                Continue For
            End If

            For Each tag As String In tags
                Dim count As Integer = 0
                Call frequency.TryGetValue(tag, count)
                frequency(tag) = count + 1
            Next

            Call matrix.Ids.Add(pkg.package_id.ToLowerInvariant())
            Call matrix.Displays.Add(pkg.package_id)
            Call sets.Add(tags)
            matrix.TagSets(matrix.Ids.Last()) = tags
        Next

        matrix.Tags = frequency _
            .OrderByDescending(Function(item) item.Value) _
            .ThenBy(Function(item) item.Key, StringComparer.OrdinalIgnoreCase) _
            .Select(Function(item) item.Key) _
            .ToList()

        If matrix.Ids.Count = 0 OrElse matrix.Tags.Count = 0 Then
            matrix.Data = New Double()() {}
            Return matrix
        End If

        ' the column index of every tag so that the rows can be filled in one pass
        Dim columns As New Dictionary(Of String, Integer)(StringComparer.OrdinalIgnoreCase)

        For i As Integer = 0 To matrix.Tags.Count - 1
            columns(matrix.Tags(i)) = i
        Next

        matrix.Data = New Double(matrix.Ids.Count - 1)() {}

        For i As Integer = 0 To sets.Count - 1
            Dim row As Double() = New Double(matrix.Tags.Count - 1) {}

            For Each tag As String In sets(i)
                row(columns(tag)) = 1
            Next

            matrix.Data(i) = row
        Next

        Return matrix
    End Function

    ''' <summary>
    ''' build a short stable hash of the feed content (the id, the version and
    ''' the tag set of every latest package) which is used to decide whether the
    ''' analysis has to run again.
    ''' </summary>
    ''' <param name="latest">the latest version of every package.</param>
    ''' <returns>the hexadecimal fingerprint.</returns>
    Public Function ComputeFingerprint(latest As List(Of PackageRecord)) As String
        Dim sb As New StringBuilder

        If latest IsNot Nothing Then
            For Each pkg As PackageRecord In latest _
                    .OrderBy(Function(p) p.package_id, StringComparer.OrdinalIgnoreCase)

                Call sb.Append(pkg.package_id.ToLowerInvariant()) _
                    .Append("|").Append(pkg.version) _
                    .Append("|").Append(normalizeTags(pkg.tags)) _
                    .Append(vbLf)
            Next
        End If

        Using sha As SHA256 = SHA256.Create()
            Dim hash As Byte() = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))
            Dim text As New StringBuilder

            For i As Integer = 0 To 15
                Call text.Append(hash(i).ToString("x2"))
            Next

            Return text.ToString()
        End Using
    End Function

    Private Function normalizeTags(tags As String) As String
        Dim list As String() = NugetStatistics _
            .SplitTags(tags) _
            .Select(Function(tag) tag.Trim().ToLowerInvariant()) _
            .Where(Function(tag) Not tag.StringEmpty()) _
            .Distinct() _
            .OrderBy(Function(tag) tag, StringComparer.Ordinal) _
            .ToArray()

        Return String.Join(" ", list)
    End Function

#End Region

#Region "helpers"

    ''' <summary>
    ''' translate the embedding so that its centroid is the origin. the umap
    ''' manifold is translation invariant, so the relative geometry (and thus
    ''' the clustering) is unchanged while the coordinates become stable and
    ''' centered for storage / rendering.
    ''' </summary>
    ''' <param name="embedding">the raw umap embedding.</param>
    ''' <returns>the centered embedding.</returns>
    Private Function center(embedding As Double()()) As Double()()
        If embedding Is Nothing OrElse embedding.Length = 0 Then
            Return embedding
        End If

        Dim measure As Double() = New Double(EmbeddingDimensions - 1) {}

        For Each vector As Double() In embedding
            For i As Integer = 0 To Math.Min(measure.Length, vector.Length) - 1
                measure(i) += vector(i)
            Next
        Next

        For i As Integer = 0 To measure.Length - 1
            measure(i) /= embedding.Length
        Next

        Dim centered As Double()() = New Double(embedding.Length - 1)() {}

        For i As Integer = 0 To embedding.Length - 1
            Dim vector As Double() = resize(embedding(i), EmbeddingDimensions)
            Dim row As Double() = New Double(EmbeddingDimensions - 1) {}

            For j As Integer = 0 To EmbeddingDimensions - 1
                row(j) = vector(j) - measure(j)
            Next

            centered(i) = row
        Next

        Return centered
    End Function

    Private Function resize(vector As Double(), size As Integer) As Double()
        Dim result As Double() = New Double(size - 1) {}

        If vector Is Nothing Then
            Return result
        End If

        For i As Integer = 0 To Math.Min(size, vector.Length) - 1
            result(i) = vector(i)
        Next

        Return result
    End Function

    Private Function stateValue(state As String, name As String) As String
        Try
            Using document As JsonDocument = JsonDocument.Parse(state)
                Dim element As JsonElement

                If document.RootElement.TryGetProperty(name, element) Then
                    Return element.ToString()
                End If
            End Using
        Catch ex As Exception
            ' a corrupted state document simply forces one more rebuild
            Call ex.Message.debug()
        End Try

        Return ""
    End Function

    Private Function stateInteger(state As String, name As String) As Integer
        Dim value As Integer

        If Integer.TryParse(stateValue(state, name), NumberStyles.Integer, CultureInfo.InvariantCulture, value) Then
            Return value
        End If

        Return 0
    End Function

#End Region
End Module
