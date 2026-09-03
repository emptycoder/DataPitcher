using System.Text.Json;
using DataPitcher.Core.Selection;

namespace DataPitcher.Application.Selection;

/// <summary>Turns the typed parameter values saved with a selection into driver parameters.</summary>
public static class SelectionParameters
{
    public static SelectionSqlParameter FromJson(string name, string kind, JsonElement value) =>
        kind switch
        {
            "int" => new SelectionSqlParameter(name, typeof(int), value.GetInt32()),
            "decimal" => new SelectionSqlParameter(name, typeof(decimal), value.GetDecimal()),
            "boolean" => new SelectionSqlParameter(name, typeof(bool), value.GetBoolean()),
            "date" => new SelectionSqlParameter(name, typeof(DateOnly), DateOnly.Parse(value.GetString()!)),
            "time" => new SelectionSqlParameter(name, typeof(TimeOnly), TimeOnly.Parse(value.GetString()!)),
            "dateTime" => new SelectionSqlParameter(name, typeof(DateTime), value.GetDateTime()),
            "guid" => new SelectionSqlParameter(name, typeof(Guid), value.GetGuid()),
            "string" => new SelectionSqlParameter(
                name,
                typeof(string),
                value.GetString() ?? throw new InvalidOperationException("Selection parameter value is required.")
            ),
            _ => throw new InvalidOperationException("Selection parameter kind is not supported."),
        };

    public static SelectionSqlParameter FromJson(JsonElement parameter) =>
        FromJson(
            parameter.GetProperty("Name").GetString()
                ?? throw new InvalidOperationException("Selection parameter name is required."),
            parameter.GetProperty("Kind").GetString() ?? "",
            parameter.GetProperty("Value")
        );
}
