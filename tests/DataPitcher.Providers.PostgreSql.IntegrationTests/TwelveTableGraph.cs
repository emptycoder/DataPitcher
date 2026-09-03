namespace DataPitcher.Providers.PostgreSql.IntegrationTests;

/// <summary>
/// The anonymised shape of the reported incident: twelve tables in three schemas, shared parents that tie the
/// closure generations, a three-table cycle (t12 -> t9 -> t11 -> t12), a two-table cycle (t12 &lt;-&gt; t11) and a
/// self reference on the root. Which cycle-closing columns are nullable decides whether an order exists. Schemas
/// are suffixed with the scope so scopes sharing a database do not collide.
/// </summary>
internal sealed class TwelveTableGraph(string scope)
{
    public string A { get; } = scope + "_a";
    public string B { get; } = scope + "_b";
    public string C { get; } = scope + "_c";

    public string Source(bool nullableT11ToT12, bool nullableT12ToT11) =>
        Tables(nullableT11ToT12, nullableT12ToT11)
        + $" INSERT INTO {A}.t3 (id) VALUES (1),(2);"
        + $" INSERT INTO {A}.t2 (id, t3id) VALUES (1,1),(2,2);"
        + $" INSERT INTO {A}.t1 (id, t2id) VALUES (1,1),(2,2);"
        + $" INSERT INTO {A}.t4 (id, t1id) VALUES (1,1);"
        + $" INSERT INTO {C}.t8 (id, t2id) VALUES (1,1);"
        + $" INSERT INTO {B}.t10 (id) VALUES (1),(2);"
        + $" INSERT INTO {B}.t11 (id, t10id, t12ida, t12idb) VALUES (1,1,1,2),(2,2,2,1);"
        + $" INSERT INTO {B}.t9 (id, t1id, t2id, t10id, t11id) VALUES (1,1,1,1,1),(2,2,2,2,2);"
        + $" INSERT INTO {B}.t12 (id, t9id, t10id, t11id, selfid) VALUES (1,1,1,1,NULL),(2,2,2,2,1);"
        + ForeignKeys();

    public string Target(bool nullableT11ToT12, bool nullableT12ToT11) =>
        Tables(nullableT11ToT12, nullableT12ToT11) + ForeignKeys();

    private string Tables(bool nullableT11ToT12, bool nullableT12ToT11) =>
        $"CREATE SCHEMA {A}; CREATE SCHEMA {B}; CREATE SCHEMA {C};"
        + $" CREATE TABLE {A}.t3 (id integer NOT NULL CONSTRAINT pk_t3 PRIMARY KEY);"
        + $" CREATE TABLE {A}.t2 (id integer NOT NULL CONSTRAINT pk_t2 PRIMARY KEY, t3id integer NOT NULL);"
        + $" CREATE TABLE {A}.t1 (id integer NOT NULL CONSTRAINT pk_t1 PRIMARY KEY, t2id integer NOT NULL);"
        + $" CREATE TABLE {A}.t4 (id integer NOT NULL CONSTRAINT pk_t4 PRIMARY KEY, t1id integer NOT NULL);"
        + $" CREATE TABLE {C}.t8 (id integer NOT NULL CONSTRAINT pk_t8 PRIMARY KEY, t2id integer NOT NULL);"
        + $" CREATE TABLE {B}.t10 (id integer NOT NULL CONSTRAINT pk_t10 PRIMARY KEY);"
        + $" CREATE TABLE {B}.t11 (id integer NOT NULL CONSTRAINT pk_t11 PRIMARY KEY, t10id integer NOT NULL, t12ida integer {Null(nullableT11ToT12)}, t12idb integer {Null(nullableT11ToT12)});"
        + $" CREATE TABLE {B}.t9 (id integer NOT NULL CONSTRAINT pk_t9 PRIMARY KEY, t1id integer NOT NULL, t2id integer NOT NULL, t10id integer NOT NULL, t11id integer NOT NULL);"
        + $" CREATE TABLE {B}.t12 (id integer NOT NULL CONSTRAINT pk_t12 PRIMARY KEY, t9id integer NOT NULL, t10id integer NOT NULL, t11id integer {Null(nullableT12ToT11)}, selfid integer NULL);";

    // Constraints are added after the rows exist so that the cyclic data can be created with every key enforced.
    private string ForeignKeys() =>
        $" ALTER TABLE {A}.t2 ADD CONSTRAINT fk_t2_t3 FOREIGN KEY (t3id) REFERENCES {A}.t3(id);"
        + $" ALTER TABLE {A}.t1 ADD CONSTRAINT fk_t1_t2 FOREIGN KEY (t2id) REFERENCES {A}.t2(id);"
        + $" ALTER TABLE {A}.t4 ADD CONSTRAINT fk_t4_t1 FOREIGN KEY (t1id) REFERENCES {A}.t1(id);"
        + $" ALTER TABLE {C}.t8 ADD CONSTRAINT fk_t8_t2 FOREIGN KEY (t2id) REFERENCES {A}.t2(id);"
        + $" ALTER TABLE {B}.t11 ADD CONSTRAINT fk_t11_t10 FOREIGN KEY (t10id) REFERENCES {B}.t10(id);"
        + $" ALTER TABLE {B}.t11 ADD CONSTRAINT fk_t11_t12a FOREIGN KEY (t12ida) REFERENCES {B}.t12(id);"
        + $" ALTER TABLE {B}.t11 ADD CONSTRAINT fk_t11_t12b FOREIGN KEY (t12idb) REFERENCES {B}.t12(id);"
        + $" ALTER TABLE {B}.t9 ADD CONSTRAINT fk_t9_t1 FOREIGN KEY (t1id) REFERENCES {A}.t1(id);"
        + $" ALTER TABLE {B}.t9 ADD CONSTRAINT fk_t9_t2 FOREIGN KEY (t2id) REFERENCES {A}.t2(id);"
        + $" ALTER TABLE {B}.t9 ADD CONSTRAINT fk_t9_t10 FOREIGN KEY (t10id) REFERENCES {B}.t10(id);"
        + $" ALTER TABLE {B}.t9 ADD CONSTRAINT fk_t9_t11 FOREIGN KEY (t11id) REFERENCES {B}.t11(id);"
        + $" ALTER TABLE {B}.t12 ADD CONSTRAINT fk_t12_t9 FOREIGN KEY (t9id) REFERENCES {B}.t9(id);"
        + $" ALTER TABLE {B}.t12 ADD CONSTRAINT fk_t12_t10 FOREIGN KEY (t10id) REFERENCES {B}.t10(id);"
        + $" ALTER TABLE {B}.t12 ADD CONSTRAINT fk_t12_t11 FOREIGN KEY (t11id) REFERENCES {B}.t11(id);"
        + $" ALTER TABLE {B}.t12 ADD CONSTRAINT fk_t12_self FOREIGN KEY (selfid) REFERENCES {B}.t12(id);";

    private static string Null(bool nullable) => nullable ? "NULL" : "NOT NULL";
}
