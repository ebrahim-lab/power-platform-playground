# 01 — Field Safety Kit

A Power Platform solution that checks whether a location is safe to send a field engineer to — based on recent seismic activity near that coordinate.

Built as a custom connector + Power Automate flow, designed to plug into Field Service work order routing in Dynamics 365.

---

## The problem

Field Service dispatchers assign engineers to locations without knowing whether something dangerous happened nearby recently. An earthquake 80km away from a job site is relevant information before you send someone. This solution surfaces that data from inside Power Platform — no external dashboards, no manual checks.

---

## What's in the solution

### Custom Connector — Field Safety Connector

Wraps the [USGS Earthquake Hazards API](https://earthquake.usgs.gov/fdsnws/event/1/). No authentication required — USGS is fully public.

**Action: GetNearbyEarthquakes**

Sends a radius-based query to USGS and transforms the raw GeoJSON response into a flat, usable structure before returning it to the caller.

Parameters:

| Parameter | Required | Default | Description |
|-----------|----------|---------|-------------|
| Latitude | Yes | — | Center point latitude |
| Longitude | Yes | — | Center point longitude |
| Radius (km) | Yes | 100 | True radius search — not a bounding box |
| Start Date | Yes | — | YYYY-MM-DD |
| End Date | Yes | — | YYYY-MM-DD |
| Min Magnitude | Yes | 4.0 | 0–10 Richter scale |
| Result Limit | No | 20 | Max events returned |

The `format` and `orderby` parameters are marked internal — callers never see them. The connector always requests GeoJSON ordered by time.

**Response transformation**

USGS returns deeply nested GeoJSON with ~20 fields per event, most of which are irrelevant to a dispatch decision. The connector's C# script strips that down to what matters:

```json
{
  "count": 3,
  "events": [
    {
      "id": "us6000sn06",
      "title": "M 4.3 - 51 km NW of Tyre, Lebanon",
      "magnitude": 4.3,
      "place": "51 km NW of Tyre, Lebanon",
      "time": 1775461935778,
      "tsunami": 0,
      "latitude": 33.5833,
      "longitude": 34.7858,
      "depth_km": 10,
      "url": "https://earthquake.usgs.gov/earthquakes/eventpage/us6000sn06"
    }
  ]
}
```

The transformation isn't just about hiding complexity. The goal is a consistent event shape regardless of which hazard source the data comes from — USGS, ACLED, NOAA, or anything else added later. Each source has its own response structure and terminology. The connector layer normalises all of them into the same format so the flow and any consuming app don't need to know or care which API produced the data.

---

### Cloud Flow — CheckLocationRisk

Instant flow. Takes a location and date range, calls the connector, and returns a structured risk assessment.

**Inputs**

| Input | Type | Description |
|-------|------|-------------|
| Location Latitude | Number | Work order or job site latitude |
| Location Longitude | Number | Work order or job site longitude |
| Start Date | Date | Beginning of the check window |
| End Date | Date | End of the check window |

Radius (500km), minimum magnitude (4.0), and result limit (20) are currently set inside the flow. These are business policy decisions that could be promoted to flow inputs or pulled from Dataverse Environment Variables depending on deployment needs — left hardcoded for now to keep the initial implementation simple.

**Retry policy**

The connector call uses exponential backoff: 5 retries starting at 5 second intervals. A transient USGS failure shouldn't fail a work order check.

**Logic**

```
Call GetNearbyEarthquakes
│
├── API returned 200?
│   ├── YES → events.length > 0?
│   │         ├── YES → flag: true,  HTTP 200
│   │         └── NO  → flag: false, HTTP 200
│   │
│   └── NO  → flag: null, HTTP 500
```

Three distinct states. `null` is intentional — a failed API call and a genuine all-clear are not the same thing, and the caller needs to handle them differently.

**Response shape**

```json
{
  "flag": true,
  "count": 3,
  "message": "3 earthquake(s) detected within 500km between 2025-01-01 and 2026-04-08",
  "events": [...]
}
```

| flag | Meaning |
|------|---------|
| `true` | Seismic activity detected — review before dispatching |
| `false` | No activity in range for the selected period |
| `null` | Data unavailable — human decision required |

---

## Use cases

**Work order pre-check in Dynamics 365 Field Service**
A plugin fires on Work Order creation, passes the location and scheduled date range to this flow, and writes the flag back to a custom field on the Work Order. The dispatcher sees it on the Schedule Board before assigning a booking.

**Power Pages portal**
A tenant submits a maintenance request. Before confirming the visit, the portal calls this flow and shows the risk message inline. High-risk locations get flagged for coordinator review.

**Canvas app dispatch tool**
A dispatcher enters a postcode, the app resolves it to coordinates, calls this flow, and shows a risk indicator before confirming the engineer assignment.

---

## Planned additions

The connector is designed to grow. Each new hazard source becomes a new action on the same connector. The flow calls whichever actions are relevant and combines the results.

| Source | Data | Auth | Status |
|--------|------|------|--------|
| USGS | Seismic events by radius | None | ✅ Built |
| ACLED | Armed conflict events by bounding box | OAuth Bearer | 🔲 Under investigation |
| NOAA | Severe weather alerts | API key | 🔲 Planned |

When ACLED is added, the flow gains a second connector call alongside the USGS call. The response shape stays the same — the `events` array contains events from multiple sources, each tagged with a `source` field. The Route Request policy handles the URL switching between USGS and ACLED within the same connector definition.

**Error response format**

The error branch currently returns a plain HTTP 500 with the flag/count/message shape. This will be updated to follow a standard error response format (RFC 7807 Problem Details) for consistency — deferred for now to keep the initial build simple.

**Location risk cache — Dataverse**

A planned improvement is to store risk assessments as Dataverse records keyed by location + radius + date range. Before calling the flow on each work order assignment, the system checks whether a risk record already exists for that area. If it does, it uses the cached result directly.

This has two benefits. First, it avoids redundant API calls when multiple engineers are being dispatched to the same area on the same day. Second, it allows dispatchers and coordinators to manually set a risk flag on a location — marking an area as high risk without waiting for an automated check. That manual flag becomes the first line of defence, with the flow acting as the fallback for locations that haven't been assessed yet.

---

## Solution structure

```
FieldSafetyKit/
├── Connectors/
│   ├── fsk_field-safety-connector_openapidefinition.json    ← connector actions + parameters
│   ├── fsk_field-safety-connector_customcodeblobcontent.csx ← C# response transformation
│   └── fsk_field-safety-connector_policytemplateinstances.json
└── Workflows/
    └── CheckLocationRisk.json  ← flow definition
```
