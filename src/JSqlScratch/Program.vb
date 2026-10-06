Imports JSql.Engine

Module Program

    Function Main(args As String()) As Integer
        Dim dir As String = IO.Path.Combine(IO.Path.GetTempPath(), "jsql-scratch-" & Guid.NewGuid().ToString("N"))
        Dim engine As New SqlEngine(dir)

        Call engine.Execute("CREATE DATABASE IF NOT EXISTS t")
        Call engine.Execute("USE t")
        Call engine.Execute("CREATE TABLE IF NOT EXISTS bl (id INT NOT NULL PRIMARY KEY, domain VARCHAR(255))")
        Call engine.Execute("INSERT INTO bl (id, domain) VALUES (1, 'spam.example.com')")

        Dim before As ResultSet = engine.Execute("SELECT * FROM bl")
        Call Console.WriteLine($"before: {before.Rows.Count}")

        Dim del As ResultSet = engine.Execute("DELETE FROM bl WHERE domain = 'spam.example.com'")
        Call Console.WriteLine($"delete: {del.Message}")

        Dim after As ResultSet = engine.Execute("SELECT * FROM bl")
        Call Console.WriteLine($"after: {after.Rows.Count}")

        ' control: delete by integer id
        Call engine.Execute("INSERT INTO bl (id, domain) VALUES (2, 'other.example.com')")
        Dim del2 As ResultSet = engine.Execute("DELETE FROM bl WHERE id = 2")
        Call Console.WriteLine($"delete by id: {del2.Message}")

        Dim after2 As ResultSet = engine.Execute("SELECT * FROM bl")
        Call Console.WriteLine($"after by id: {after2.Rows.Count}")

        ' boolean where
        Call engine.Execute("CREATE TABLE IF NOT EXISTS uf (id INT NOT NULL PRIMARY KEY, email VARCHAR(320), official BOOLEAN)")
        Call engine.Execute("INSERT INTO uf (id, email, official) VALUES (1, 'a@b.c', TRUE)")
        Dim q As ResultSet = engine.Execute("SELECT email FROM uf WHERE official = TRUE")
        Call Console.WriteLine($"bool where hits: {q.Rows.Count}")

        ' string select where
        Dim q2 As ResultSet = engine.Execute("SELECT id FROM bl WHERE domain = 'spam.example.com'")
        Call Console.WriteLine($"string select where hits: {q2.Rows.Count}")

        Call engine.DisposeSafe()
        Return 0
    End Function
End Module
