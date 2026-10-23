plugins {
    alias(libs.plugins.android.library)
    alias(libs.plugins.kotlin.serialization)
}

android {
    namespace = "com.passwordmanagement.core"
    compileSdk = libs.versions.compileSdk.get().toInt()

    defaultConfig {
        minSdk = libs.versions.minSdk.get().toInt()
        consumerProguardFiles("consumer-rules.pro")
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

// The host JVM cannot load the Android .so, so unit tests use lazysodium-java instead.
configurations.configureEach {
    if (name.endsWith("UnitTestRuntimeClasspath")) exclude(group = "com.goterl", module = "lazysodium-android")
}

dependencies {
    implementation(libs.kotlinx.serialization.json)
    implementation(libs.lazysodium.android) { artifact { type = "aar" } }
    implementation(libs.jna) { artifact { type = "aar" } }

    testImplementation(libs.junit)
    // Desktop libsodium for host-JVM unit tests; test scope only, never reaches the APK.
    testImplementation(libs.lazysodium.java)
}
