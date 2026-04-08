public class Script : ScriptBase
{
    public override async Task<HttpResponseMessage> ExecuteAsync()
    {
        // Forward the request to USGS
        var response = await this.Context.SendAsync(
            this.Context.Request,
            this.CancellationToken
        ).ConfigureAwait(false);

        // Read the raw response body
        var rawBody = await response.Content.ReadAsStringAsync()
            .ConfigureAwait(false);

        var raw = JObject.Parse(rawBody);
        var features = (JArray)raw["features"];

        var events = new JArray();

        foreach (var feature in features)
        {
            var props = feature["properties"];
            var coords = feature["geometry"]["coordinates"];

            events.Add(new JObject
            {
                ["id"]        = feature["id"],
                ["title"]     = props["title"],
                ["magnitude"] = props["mag"],
                ["place"]     = props["place"],
                ["time"]      = props["time"],
                ["tsunami"]   = props["tsunami"],
                ["longitude"] = coords[0],
                ["latitude"]  = coords[1],
                ["depth_km"]  = coords[2],
                ["url"]       = props["url"]
            });
        }

        var clean = new JObject
        {
            ["count"]  = events.Count,
            ["events"] = events
        };

        response.Content = CreateJsonContent(clean.ToString());
        return response;
    }
}