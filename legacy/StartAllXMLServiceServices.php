<?php

$directory = 'C:\\Artsoft\\ARTSOFTServicos';
$cfgFiles = glob($directory . '\\*.cfg');

/**
 * Query services once
 */
$psScript = '@(Get-CimInstance Win32_Service | Select-Object Name, PathName, StartMode, State) | ConvertTo-Json -Compress';
$encoded = base64_encode(mb_convert_encoding($psScript, 'UTF-16LE'));

exec("powershell -NoProfile -EncodedCommand $encoded", $psOutput, $exitCode);

if ($exitCode !== 0 || empty($psOutput)) {
    die("ERROR: Failed to query Windows services\n");
}

$services = json_decode(implode('', $psOutput), true);

if (!is_array($services)) {
    die("ERROR: Invalid JSON from PowerShell\n");
}

foreach ($cfgFiles as $cfgFile) {

    echo "\nProcessing: $cfgFile\n";

    $cfgPath = strtolower($cfgFile);
    $serviceName = null;
    $serviceState = null;
    $startMode = null;

    /**
     * Find service linked to this cfg
     */
    foreach ($services as $service) {
        if (!empty($service['PathName']) &&
            stripos($service['PathName'], $cfgPath) !== false) {

            $serviceName  = $service['Name'];
            $serviceState = $service['State'];
            $startMode    = $service['StartMode'];
            break;
        }
    }

    if (!$serviceName) {
        echo "Service not found for CFG\n";
        continue;
    }

    /**
     * Ensure delayed auto start
     */
    if (strtolower($startMode) !== 'auto') {
        echo "Setting delayed auto start for $serviceName\n";
        exec("sc config \"$serviceName\" start= delayed-auto");
    } else {
        echo "Startup already automatic\n";
    }

    /**
     * Start service if stopped
     */
    if (strtolower($serviceState) !== 'running') {

        echo "Starting service $serviceName\n";

        exec("sc start \"$serviceName\"", $startOutput, $startCode);

        if ($startCode !== 0) {
            echo "Failed to start service\n";
        } else {
            echo "Service started\n";
        }

    } else {
        echo "Service already running\n";
    }
}

echo "\nDone.\n";