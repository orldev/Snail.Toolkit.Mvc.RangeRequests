## Snail.Toolkit.Mvc.RangeRequests

HTTP Range Requests (RFC 7233) served from a delegate, as an ASP.NET Core MVC `ActionResult` or a Minimal
API `IResult`.

### Why this exists

ASP.NET Core already answers range requests for anything that is a `Stream`: set
`FileStreamResult.EnableRangeProcessing` and the framework parses `Range`, evaluates the RFC 7232
preconditions and writes the partial body for you.

This library covers the case the framework does not: an entity that is **not** a `Stream` — an object store
answering a ranged `GET`, a database blob, generated content — where buffering the whole thing into memory
just to serve 200 KB of it is the wrong trade. You hand it a delegate that writes one range, and it takes
care of the protocol around that delegate.

If your bytes already live in a `Stream`, use the framework. That is not a limitation of this library; it is
the reason its surface is this small.

### Usage

```csharp
using Snail.Toolkit.Mvc.RangeRequests;
using Snail.Toolkit.Mvc.RangeRequests.Extensions;

var content = new RangeContent
{
    Write = (body, offset, length, token) => store.CopyRangeToAsync(key, offset, length, body, token),
    Length = metadata.Length,
    ContentType = metadata.ContentType,
    LastModified = metadata.LastModified,
    ETag = metadata.Version,
    FileName = metadata.Name
};

// MVC
public IActionResult Get() => this.RangeFile(content);

// Minimal API
app.MapGet("/media/{key}", () => Results.Extensions.RangeFile(content));
```

Both extensions return the concrete result type, so nothing is lost by using them: `Results.Extensions`
handing back an `IResult` would hide the endpoint metadata routing reads off the declared return type.
Constructing `new RangeFileResult(content)` or `new RangeFileHttpResult(content)` by hand does the same
thing.

`LastModified` feeds `Last-Modified` and, unless you name an `ETag`, the validator as well, so it has to
stay stable for the lifetime of the entity. A value that drifts between requests makes every resumed
download restart from zero.

### The delegate's contract

`Write` is the one thing you supply, and three rules apply to it:

- **Write exactly `length` bytes.** `Content-Length` is already on the wire by the time it runs; a short
  write is rejected by the client and a long one is rejected by Kestrel.
- **Be safe to call concurrently and re-entrantly.** One `RangeContent` normally serves every request for an
  entity, and a multipart response calls the delegate once per part. Anything it captures must tolerate that.
- **Let cancellation surface.** A token that fires means the client left; swallowing it turns a routine
  disconnect into an error.

Retries and circuit breakers belong inside the delegate, not around it — this library never leaves the
process, so there is nothing here to retry.

### Validators

Name an `ETag` whenever your store has one — an object version, a content hash, a row version.

Left unset, the tag is derived from `LastModified` and `Length`. HTTP dates carry whole seconds, so two
entities of equal length stored within the same second get the same tag, and a client then serves a stale
body out of its own cache. Deriving it is fine for content whose length or timestamp always moves; it is not
fine for a store that overwrites objects in place.

Weak validators (`W/"…"`) are refused rather than accepted quietly: RFC 7232 §2.1 bars them from the strong
comparison `If-Range` requires, so a weak tag would turn every resumed download into a fresh one.

### Validation

Values are checked where they enter, because `required` guarantees only that a member was set:

| Rejected                                            | Because                                                                     |
|-----------------------------------------------------|-----------------------------------------------------------------------------|
| `Length` below zero                                 | reaches `Response.ContentLength` and your delegate as a count                |
| `ContentType` that is not a media type              | it is written into the multipart **body**, past Kestrel's header validation  |
| `ETag` that is not an entity tag, or is weak        | it is a validator clients compare byte for byte, or one they cannot resume against |
| `MaxRanges` below one                               | silently disables range serving                                              |
| `MaxRangeLength` or `MaxFileLength` of zero or less | describes a range that ends before it starts, or refuses every entity        |

`ContentType` and `ETag` are stored in the canonical form their header types parse them into, so a value
carrying CRLF cannot be represented at all.

### Limits

`RangeRules` bounds what the server is willing to serve:

| Rule             | Default | What it does                                                        |
|------------------|---------|---------------------------------------------------------------------|
| `MaxRanges`      | `5`     | More ranges than this and the header is ignored, serving `200`      |
| `MaxFileLength`  | none    | Larger entities answer `413`                                        |
| `MaxRangeLength` | none    | Longer ranges are truncated, which RFC 7233 §4.1 permits            |

`MaxRanges` is not a tuning knob. An unbounded byte-range-set lets one request ask for the entity many times
over — `bytes=0-,0-,0-…` repeated ten thousand times over a 1 GB entity promises 10 TB — which is the shape
of CVE-2011-3192. Overlapping and adjacent ranges are coalesced for the same reason.

### OpenAPI

`RangeFileHttpResult` provides its own endpoint metadata, so a Minimal API endpoint describes `200`, `206`,
`304`, `412`, `413` and `416` without a single `Produces` call. Without it a generated client is told about
`200` alone and treats the `206` it actually receives as a failure.

`IStatusCodeHttpResult` is deliberately not implemented: the status is decided per request from headers no
build-time description can see, so it could only answer `null`.

### Types

| Type                       | Role                                                              |
|----------------------------|-------------------------------------------------------------------|
| `RangeContent`             | The entity to serve and how to write it                            |
| `RangeFileResult`          | MVC adapter, also `controller.RangeFile(content)`                  |
| `RangeFileHttpResult`      | Minimal API adapter, also `Results.Extensions.RangeFile(content)`  |
| `RangeRules`               | What the server is willing to serve                                |
| `ByteRanges`               | Reads a `Range` header against an entity of known length           |
| `ByteRange`                | One satisfiable range, both ends inclusive                         |
| `ByteRangeSet`             | Whether the header was ignored, satisfiable or unsatisfiable       |
| `WriteRange`               | The delegate that writes one range                                 |

### Behaviour

- An unreadable `Range` header is ignored and the entity served whole, per RFC 7233 §3.1 — never rejected.
- A syntactically valid but unsatisfiable range answers `416` with `Content-Range: bytes */length`.
- Preconditions are evaluated in the order RFC 7232 §6 prescribes: `If-Match`, `If-Unmodified-Since`,
  `If-None-Match`, `If-Modified-Since`, and only then `Range`.
- Offsets are `long` end to end, so entities past 2 GB are served correctly.
- A failure after `Content-Length` has been sent aborts the connection rather than handing the client a
  truncated body under a success status.
- Log entries are written under the category of the result you used, at levels set by who has to act: a
  failed body is the only `Error`.

### Documentation
- [Hypertext Transfer Protocol (HTTP/1.1): Range Requests](https://www.rfc-editor.org/rfc/rfc7233)
- [Hypertext Transfer Protocol (HTTP/1.1): Conditional Requests](https://www.rfc-editor.org/rfc/rfc7232)
- [CHANGELOG](CHANGELOG.md) — what changed and why

### Sourcecode
- [The original source](https://github.com/tpeczek/Lib.Web.Mvc/blob/main/Lib.Web.Mvc)

### License

[MIT](LICENSE)
