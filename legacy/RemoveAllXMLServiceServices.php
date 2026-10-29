<?php
$blacklist = [
    'PortalColab.cfg',
    'ENotif.cfg',
];
// Define the directory containing the .cfg files
$directory = 'C:\Artsoft\ARTSOFTServicos';

// Get all .cfg files in the directory
$cfgFiles = glob($directory . '\*.cfg');

// Loop through each .cfg file and run the command
foreach ($cfgFiles as $cfgFile) {
    // Get the filename from the full path
    $fileName = basename($cfgFile);
    if(in_array($fileName,$blacklist)){
        continue;
    }

    // Prepare the command
    $command = ".\\ArtExecSvc C:\Artsoft\ARTSOFTServicos\\$cfgFile -remove";

    // Open a process with popen
    $process = popen($command, 'r');

    if (is_resource($process)) {
        // Wait for the prompt and send 'S' for confirmation
        while (!feof($process)) {
            $line = fgets($process);
            echo $line; // Optional: display output for debugging
            if (strpos($line, "Pretende executar este servico com") !== false) {
                fwrite($process, "S\n");
            }
        }
        pclose($process);
        echo "Successfully executed command for $fileName.\n";
    } else {
        echo "Failed to execute command for $fileName.\n";
    }
}