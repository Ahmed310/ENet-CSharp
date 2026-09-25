APP_PLATFORM := android-21

APP_ABI := armeabi-v7a arm64-v8a x86_64
APP_STL := none

APP_OPTIM := release
APP_SHORT_COMMANDS := true

APP_CPPFLAGS += -fPIC
APP_CFLAGS += -fPIC

# 16 KB page support (Android 15+ devices, Google Play): align 64-bit libraries to 16 KB. NDK r27 reads
# this switch; r28 and later align by default. Android.mk also passes the linker flag, for any NDK.
APP_SUPPORT_FLEXIBLE_PAGE_SIZES := true