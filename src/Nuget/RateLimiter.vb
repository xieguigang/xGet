''' <summary>
''' a thread safe in process sliding window rate limiter for the unauthenticated
''' endpoints of the nuget server (``/api/register`` and ``/api/reset``): every
''' key keeps the unix timestamps of its hits inside the configured window, and
''' a new hit is accepted only while the window holds fewer hits than the
''' configured maximum.
'''
''' the expired timestamps are removed lazily on every access, so no background
''' cleanup thread is required; a sweep of stale keys keeps the memory bounded.
''' the counters are not persisted: they reset when the server process restarts,
''' which is acceptable because the limiter is an anti abuse measure and not an
''' audit log.
''' </summary>
''' <remarks>
''' the admission test and the timestamp enqueue run inside one lock, so a
''' request is either counted on every key of its limit set or on none of it:
''' a request which passes the email limit but exceeds the ip limit leaves no
''' hit on the email key.
''' </remarks>
Public Class RateLimiter

    ''' <summary>one keyed admission rule of a request.</summary>
    Public Structure RateLimit

        ''' <summary>the limiter key, for example ``email:user@example.com`` or ``ip:10.0.0.1``.</summary>
        Public ReadOnly key As String

        ''' <summary>the maximum number of hits of this key inside the window.</summary>
        Public ReadOnly maxHits As Integer

        Public Sub New(key As String, maxHits As Integer)
            Me.key = key
            Me.maxHits = maxHits
        End Sub
    End Structure

    ''' <summary>the width of the sliding window in seconds.</summary>
    Private ReadOnly windowSeconds As Long

    ''' <summary>the hit timestamps of every known limiter key.</summary>
    Private ReadOnly hits As New Dictionary(Of String, Queue(Of Long))(StringComparer.Ordinal)

    ''' <summary>when the key table grows beyond this size the stale keys are swept.</summary>
    Private Const SweepThreshold As Integer = 4096

    Private ReadOnly gate As New Object

    ''' <summary>
    ''' create the limiter.
    ''' </summary>
    ''' <param name="windowSeconds">the width of the sliding window in seconds (at least 1).</param>
    Public Sub New(windowSeconds As Integer)
        Me.windowSeconds = Math.Max(1, windowSeconds)
    End Sub

    ''' <summary>
    ''' test one keyed rule.
    ''' </summary>
    ''' <param name="key">the limiter key (must not be empty).</param>
    ''' <param name="maxHits">the maximum number of hits inside the window.</param>
    ''' <returns><c>True</c> when the hit is admitted (and counted), <c>False</c> when the key is over its limit.</returns>
    Public Function TryAcquire(key As String, maxHits As Integer) As Boolean
        Return TryAcquire(New RateLimit(key, maxHits))
    End Function

    ''' <summary>
    ''' test one or more keyed rules atomically: the hit is admitted only when
    ''' every rule of the set accepts it, and then it is counted on every key
    ''' of the set. an empty or invalid rule set always rejects.
    ''' </summary>
    ''' <param name="limits">the keyed admission rules of this request.</param>
    Public Function TryAcquire(ParamArray limits As RateLimit()) As Boolean
        If limits Is Nothing OrElse limits.Length = 0 Then
            Return False
        End If

        For Each rule As RateLimit In limits
            If rule.key.StringEmpty() OrElse rule.maxHits <= 0 Then
                Return False
            End If
        Next

        Dim now As Long = TotpModule.GetUnixTime()
        Dim cutoff As Long = now - windowSeconds

        SyncLock gate
            ' peek: every rule must have a free slot before any key is counted
            For Each rule As RateLimit In limits
                If windowCount(rule.key, cutoff) >= rule.maxHits Then
                    Return False
                End If
            Next

            ' commit: count the admitted hit on every key
            For Each rule As RateLimit In limits
                Call queueOf(rule.key).Enqueue(now)
            Next

            If hits.Count > SweepThreshold Then
                Call sweep(cutoff)
            End If

            Return True
        End SyncLock
    End Function

    ''' <summary>
    ''' the number of hits which the given key still carries inside the window.
    ''' the expired timestamps are dequeued on the way.
    ''' </summary>
    ''' <remarks>the caller holds the <see cref="gate"/> lock.</remarks>
    Private Function windowCount(key As String, cutoff As Long) As Integer
        Dim q As Queue(Of Long) = queueOf(key)

        While q.Count > 0 AndAlso q.Peek() <= cutoff
            Call q.Dequeue()
        End While

        Return q.Count
    End Function

    ''' <summary>get (or lazily create) the timestamp queue of one key.</summary>
    ''' <remarks>the caller holds the <see cref="gate"/> lock.</remarks>
    Private Function queueOf(key As String) As Queue(Of Long)
        Dim q As Queue(Of Long) = Nothing

        If Not hits.TryGetValue(key, q) Then
            q = New Queue(Of Long)
            hits(key) = q
        End If

        Return q
    End Function

    ''' <summary>remove the keys which carry no hit inside the window anymore.</summary>
    ''' <remarks>the caller holds the <see cref="gate"/> lock.</remarks>
    Private Sub sweep(cutoff As Long)
        Dim stale As New List(Of String)

        For Each item As KeyValuePair(Of String, Queue(Of Long)) In hits
            Dim q As Queue(Of Long) = item.Value

            While q.Count > 0 AndAlso q.Peek() <= cutoff
                Call q.Dequeue()
            End While

            If q.Count = 0 Then
                Call stale.Add(item.Key)
            End If
        Next

        For Each key As String In stale
            Call hits.Remove(key)
        Next
    End Sub

    ''' <summary>
    ''' the limiter key of one email address.
    ''' </summary>
    Public Shared Function EmailKey(email As String) As String
        Return "email:" & If(email, "").Trim().ToLowerInvariant()
    End Function

    ''' <summary>
    ''' the limiter key of one client ip address.
    ''' </summary>
    Public Shared Function IpKey(ip As String) As String
        Return "ip:" & If(ip, "").Trim()
    End Function
End Class
