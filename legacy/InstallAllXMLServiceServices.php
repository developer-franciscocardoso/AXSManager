<?php

$directory = 'C:\\Artsoft\\ARTSOFTServicos';
$cfgFiles = glob($directory . '\\*.cfg');

/**
 * Load services ONCE (SAFE)
 */
$psScript = '@(Get-CimInstance Win32_Service | Select-Object Name, PathName) | ConvertTo-Json -Compress';
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

    /**
     * FAST lookup
     */
    foreach ($services as $service) {
        if (!empty($service['PathName']) &&
            stripos($service['PathName'], $cfgPath) !== false) {
            $serviceName = $service['Name'];
            break;
        }
    }

    /**
     * Install ONLY if missing
     */
    if (!$serviceName) {

        echo "Installing service...\n";

        $installCmd = ".\\ArtExecSvc.exe \"$cfgFile\" -install \"Pervasive.SQL (transactional)\"";
        $process = popen($installCmd, 'r');

        if (is_resource($process)) {
            while (!feof($process)) {
                $line = fgets($process);
                echo $line;

                if (strpos($line, "Pretende executar este servico com") !== false) {
                    fwrite($process, "S\n");
                }
            }
            pclose($process);
        }

        // Reload services ONCE after install
        exec("powershell -NoProfile -EncodedCommand $encoded", $psOutput);
        $services = json_decode(implode('', $psOutput), true);

        foreach ($services as $service) {
            if (!empty($service['PathName']) &&
                stripos($service['PathName'], $cfgPath) !== false) {
                $serviceName = $service['Name'];
                break;
            }
        }
    }

    /**
     * Set delayed start
     */
    if ($serviceName) {
        echo "Setting delayed start for $serviceName\n";
        exec("sc config \"$serviceName\" start= delayed-auto");
    } else {
        echo "ERROR: Service not found for CFG\n";
    }
}

echo "\nAll services processed successfully.\n";
