param(
    [string]$BaseUrl = "http://localhost:5137"
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Net.Http

function Assert-Status {
    param(
        [System.Net.Http.HttpResponseMessage]$Response,
        [System.Net.HttpStatusCode]$Expected,
        [string]$Label
    )

    if ($Response.StatusCode -ne $Expected) {
        $body = $Response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        throw "$Label expected HTTP $([int]$Expected) but got HTTP $([int]$Response.StatusCode): $body"
    }
}

function Assert-Redirect {
    param(
        [System.Net.Http.HttpResponseMessage]$Response,
        [string]$ExpectedLocation,
        [string]$Label
    )

    Assert-Status $Response ([System.Net.HttpStatusCode]::Redirect) $Label
    if ($Response.Headers.Location.OriginalString -ne $ExpectedLocation) {
        throw "$Label expected redirect to '$ExpectedLocation' but got '$($Response.Headers.Location)'"
    }
}

function Assert-AdminPage {
    param(
        [System.Net.Http.HttpResponseMessage]$Response,
        [Guid]$FeedId,
        [string]$Label
    )

    Assert-Status $Response ([System.Net.HttpStatusCode]::OK) $Label
    if ($Response.Content.Headers.ContentType.MediaType -ne "text/html") {
        throw "$Label expected text/html but got '$($Response.Content.Headers.ContentType)'"
    }

    $body = $Response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if (!$body.Contains("data-feed-id=`"$FeedId`"")) {
        throw "$Label did not contain the canonical feed ID"
    }
}

function New-Request {
    param(
        [System.Net.Http.HttpMethod]$Method,
        [string]$Uri,
        [string]$Authorization,
        [System.Net.Http.HttpContent]$Content
    )

    $request = [System.Net.Http.HttpRequestMessage]::new($Method, $Uri)

    if ($Authorization) {
        $null = $request.Headers.TryAddWithoutValidation("Authorization", $Authorization)
    }

    if ($Content) {
        $request.Content = $Content
    }

    return $request
}

$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect = $false
$client = [System.Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds(45)
$BaseUrl = $BaseUrl.TrimEnd("/")

try {
    $openFeedId = [Guid]::NewGuid()
    $openReaderId = [Guid]::NewGuid()
    $openFeedUrl = "$BaseUrl/$openFeedId"
    $openReaderUrl = "$openFeedUrl/$openReaderId"

    $response = $client.GetAsync("$openReaderUrl/reset").GetAwaiter().GetResult()
    Assert-Redirect $response "/$openFeedId/$openReaderId" "open reader reset"

    $response = $client.GetAsync($openFeedUrl).GetAwaiter().GetResult()
    Assert-Status $response ([System.Net.HttpStatusCode]::OK) "open feed help"

    $response = $client.GetAsync("$openFeedUrl/admin").GetAwaiter().GetResult()
    Assert-AdminPage $response $openFeedId "open feed admin page"

    $readerTask = $client.GetAsync($openReaderUrl)
    $content = [System.Net.Http.StringContent]::new("open-smoke", [System.Text.Encoding]::UTF8, "text/plain")
    $response = $client.PostAsync($openFeedUrl, $content).GetAwaiter().GetResult()
    Assert-Status $response ([System.Net.HttpStatusCode]::OK) "open feed post"

    $response = $readerTask.GetAwaiter().GetResult()
    Assert-Status $response ([System.Net.HttpStatusCode]::OK) "open feed read"
    $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if ($body -ne "open-smoke") {
        throw "open feed read returned '$body'"
    }

    $response = $client.SendAsync((New-Request ([System.Net.Http.HttpMethod]::Get) $openFeedUrl "wrong-place" $null)).GetAwaiter().GetResult()
    Assert-Status $response ([System.Net.HttpStatusCode]::Forbidden) "open feed rejects authorization"

    $protectedFeedId = [Guid]::NewGuid()
    $protectedReaderId = [Guid]::NewGuid()
    $protectedFeedUrl = "$BaseUrl/$protectedFeedId"
    $protectedReaderUrl = "$protectedFeedUrl/$protectedReaderId"
    $key = "smoke-test-key"

    $response = $client.SendAsync((New-Request ([System.Net.Http.HttpMethod]::Get) "$protectedReaderUrl/reset" $key $null)).GetAwaiter().GetResult()
    Assert-Redirect $response "/$protectedFeedId/$protectedReaderId" "protected reader reset"

    $response = $client.SendAsync((New-Request ([System.Net.Http.HttpMethod]::Get) $protectedFeedUrl $key $null)).GetAwaiter().GetResult()
    Assert-Status $response ([System.Net.HttpStatusCode]::OK) "protected feed help"

    $response = $client.GetAsync("$protectedFeedUrl/admin").GetAwaiter().GetResult()
    Assert-AdminPage $response $protectedFeedId "protected feed public admin page"

    $readerTask = $client.SendAsync((New-Request ([System.Net.Http.HttpMethod]::Get) $protectedReaderUrl $key $null))
    $content = [System.Net.Http.StringContent]::new("protected-smoke", [System.Text.Encoding]::UTF8, "text/plain")
    $response = $client.SendAsync((New-Request ([System.Net.Http.HttpMethod]::Post) $protectedFeedUrl $key $content)).GetAwaiter().GetResult()
    Assert-Status $response ([System.Net.HttpStatusCode]::OK) "protected feed post"

    $response = $readerTask.GetAwaiter().GetResult()
    Assert-Status $response ([System.Net.HttpStatusCode]::OK) "protected feed read"
    $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if ($body -ne "protected-smoke") {
        throw "protected feed read returned '$body'"
    }

    $response = $client.SendAsync((New-Request ([System.Net.Http.HttpMethod]::Get) $protectedFeedUrl "wrong-key" $null)).GetAwaiter().GetResult()
    Assert-Status $response ([System.Net.HttpStatusCode]::Unauthorized) "protected feed rejects wrong key"

    Write-Host "Smoke test passed for $BaseUrl"
}
finally {
    $client.Dispose()
}
