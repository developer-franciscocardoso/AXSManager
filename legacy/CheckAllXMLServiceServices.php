<?php

// Define the services and their respective ports to check
$services = [
    51003,
    50189,
    51066,
    50056,
    51490,
    50112,
    50019,
    51105,
    51134,
    51130,
    50028,
    50154,
    50031,
    50196,
    50195,
    51219,
    50193,
    50003,
    51590,
    50156,
    50155,
    50165,
    50167,
    50145,
    50147,
    50153,
    50157,
    50164,
    50158,
    50150,
    50149,
    50146,
    50163,
    50162,
    50166,
    50160,
    50151,
    50161,
    50105,
    50159,
    50184,
    50189,
    51511,
    50065,
    50116,
    50194,
    50079,
    51032,
    51570,
    50183,
    50192,
    50114,
    51018,
    50001,
    50188,
    50187,
    50002,
    50021,
    51815,
    51280,
    50020,
    50140,
    50095,
    50181,
    51218,
    51226,
    8000,
];

// Function to check if a service is accessible
function isServiceAccessible($port)
{
    $connection = @fsockopen('127.0.0.1', $port, $errno, $errstr, 5); // 5 seconds timeout
    if ($connection) {
        fclose($connection);
        return true;
    }
    return false;
}

// Check each service and print the result
foreach ($services as  $port) {
    if (isServiceAccessible($port)) {
        echo "Service on localhost:$port is accessible.\n";
    } else {
        echo "Service on localhost:$port is not accessible.\n";
    }
}
