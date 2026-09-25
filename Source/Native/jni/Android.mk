LOCAL_PATH := $(call my-dir)
include $(CLEAR_VARS)

LOCAL_MODULE    := libenet
LOCAL_SRC_FILES := ../enet.c

ifdef ENET_DEBUG
	LOCAL_CFLAGS += -DENET_DEBUG
endif

# Android.mk is evaluated per ABI (unlike Application.mk), so this reaches every 64-bit build with any NDK
ifneq ($(filter arm64-v8a x86_64,$(TARGET_ARCH_ABI)),)
	LOCAL_LDFLAGS += -Wl,-z,max-page-size=16384
endif

ifdef ENET_STATIC
	include $(BUILD_STATIC_LIBRARY)
else
	include $(BUILD_SHARED_LIBRARY)
endif
