namespace DataPitcher.Providers.SqlServer.IntegrationTests;

/// <summary>
/// The anonymised shape of the reported incident: twelve tables in three schemas, shared parents that tie the
/// closure generations, a three-table cycle (T12 -> T9 -> T11 -> T12), a two-table cycle (T12 &lt;-&gt; T11) and a
/// self reference on the root. Which cycle-closing columns are nullable decides whether an order exists.
/// </summary>
internal static class TwelveTableGraph
{
    public static string Source(bool nullableT11ToT12, bool nullableT12ToT11) =>
        Tables(nullableT11ToT12, nullableT12ToT11)
        + " INSERT SchemaA.T3 (Id) VALUES (1),(2);"
        + " INSERT SchemaA.T2 (Id, T3Id) VALUES (1,1),(2,2);"
        + " INSERT SchemaA.T1 (Id, T2Id) VALUES (1,1),(2,2);"
        + " INSERT SchemaA.T4 (Id, T1Id) VALUES (1,1);"
        + " INSERT SchemaC.T8 (Id, T2Id) VALUES (1,1);"
        + " INSERT SchemaB.T10 (Id) VALUES (1),(2);"
        + " INSERT SchemaB.T11 (Id, T10Id, T12IdA, T12IdB) VALUES (1,1,1,2),(2,2,2,1);"
        + " INSERT SchemaB.T9 (Id, T1Id, T2Id, T10Id, T11Id) VALUES (1,1,1,1,1),(2,2,2,2,2);"
        + " INSERT SchemaB.T12 (Id, T9Id, T10Id, T11Id, SelfId) VALUES (1,1,1,1,NULL),(2,2,2,2,1);"
        + ForeignKeys();

    public static string Target(bool nullableT11ToT12, bool nullableT12ToT11) =>
        Tables(nullableT11ToT12, nullableT12ToT11) + ForeignKeys();

    private static string Tables(bool nullableT11ToT12, bool nullableT12ToT11) =>
        "EXEC('CREATE SCHEMA SchemaA'); EXEC('CREATE SCHEMA SchemaB'); EXEC('CREATE SCHEMA SchemaC');"
        + " CREATE TABLE SchemaA.T3 (Id int NOT NULL CONSTRAINT PK_T3 PRIMARY KEY);"
        + " CREATE TABLE SchemaA.T2 (Id int NOT NULL CONSTRAINT PK_T2 PRIMARY KEY, T3Id int NOT NULL);"
        + " CREATE TABLE SchemaA.T1 (Id int NOT NULL CONSTRAINT PK_T1 PRIMARY KEY, T2Id int NOT NULL);"
        + " CREATE TABLE SchemaA.T4 (Id int NOT NULL CONSTRAINT PK_T4 PRIMARY KEY, T1Id int NOT NULL);"
        + " CREATE TABLE SchemaC.T8 (Id int NOT NULL CONSTRAINT PK_T8 PRIMARY KEY, T2Id int NOT NULL);"
        + " CREATE TABLE SchemaB.T10 (Id int NOT NULL CONSTRAINT PK_T10 PRIMARY KEY);"
        + $" CREATE TABLE SchemaB.T11 (Id int NOT NULL CONSTRAINT PK_T11 PRIMARY KEY, T10Id int NOT NULL, T12IdA int {Null(nullableT11ToT12)}, T12IdB int {Null(nullableT11ToT12)});"
        + " CREATE TABLE SchemaB.T9 (Id int NOT NULL CONSTRAINT PK_T9 PRIMARY KEY, T1Id int NOT NULL, T2Id int NOT NULL, T10Id int NOT NULL, T11Id int NOT NULL);"
        + $" CREATE TABLE SchemaB.T12 (Id int NOT NULL CONSTRAINT PK_T12 PRIMARY KEY, T9Id int NOT NULL, T10Id int NOT NULL, T11Id int {Null(nullableT12ToT11)}, SelfId int NULL);";

    // Constraints are added after the rows exist so that the cyclic data can be created with every key enforced.
    private static string ForeignKeys() =>
        " ALTER TABLE SchemaA.T2 ADD CONSTRAINT FK_T2_T3 FOREIGN KEY (T3Id) REFERENCES SchemaA.T3(Id);"
        + " ALTER TABLE SchemaA.T1 ADD CONSTRAINT FK_T1_T2 FOREIGN KEY (T2Id) REFERENCES SchemaA.T2(Id);"
        + " ALTER TABLE SchemaA.T4 ADD CONSTRAINT FK_T4_T1 FOREIGN KEY (T1Id) REFERENCES SchemaA.T1(Id);"
        + " ALTER TABLE SchemaC.T8 ADD CONSTRAINT FK_T8_T2 FOREIGN KEY (T2Id) REFERENCES SchemaA.T2(Id);"
        + " ALTER TABLE SchemaB.T11 ADD CONSTRAINT FK_T11_T10 FOREIGN KEY (T10Id) REFERENCES SchemaB.T10(Id);"
        + " ALTER TABLE SchemaB.T11 ADD CONSTRAINT FK_T11_T12A FOREIGN KEY (T12IdA) REFERENCES SchemaB.T12(Id);"
        + " ALTER TABLE SchemaB.T11 ADD CONSTRAINT FK_T11_T12B FOREIGN KEY (T12IdB) REFERENCES SchemaB.T12(Id);"
        + " ALTER TABLE SchemaB.T9 ADD CONSTRAINT FK_T9_T1 FOREIGN KEY (T1Id) REFERENCES SchemaA.T1(Id);"
        + " ALTER TABLE SchemaB.T9 ADD CONSTRAINT FK_T9_T2 FOREIGN KEY (T2Id) REFERENCES SchemaA.T2(Id);"
        + " ALTER TABLE SchemaB.T9 ADD CONSTRAINT FK_T9_T10 FOREIGN KEY (T10Id) REFERENCES SchemaB.T10(Id);"
        + " ALTER TABLE SchemaB.T9 ADD CONSTRAINT FK_T9_T11 FOREIGN KEY (T11Id) REFERENCES SchemaB.T11(Id);"
        + " ALTER TABLE SchemaB.T12 ADD CONSTRAINT FK_T12_T9 FOREIGN KEY (T9Id) REFERENCES SchemaB.T9(Id);"
        + " ALTER TABLE SchemaB.T12 ADD CONSTRAINT FK_T12_T10 FOREIGN KEY (T10Id) REFERENCES SchemaB.T10(Id);"
        + " ALTER TABLE SchemaB.T12 ADD CONSTRAINT FK_T12_T11 FOREIGN KEY (T11Id) REFERENCES SchemaB.T11(Id);"
        + " ALTER TABLE SchemaB.T12 ADD CONSTRAINT FK_T12_Self FOREIGN KEY (SelfId) REFERENCES SchemaB.T12(Id);";

    private static string Null(bool nullable) => nullable ? "NULL" : "NOT NULL";
}
