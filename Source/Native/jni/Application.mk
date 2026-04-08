APP_PLATFORM := android-31

APP_ABI := armeabi-v7a arm64-v8a x86_64
APP_STL := none

APP_OPTIM := release
APP_SHORT_COMMANDS := true

APP_CPPFLAGS += -fPIC
APP_CFLAGS += -fPIC

ifeq ($(TARGET_ARCH_ABI),arm64-v8a)
  APP_LDFLAGS += -Wl,-z,max-page-size=16384
endif