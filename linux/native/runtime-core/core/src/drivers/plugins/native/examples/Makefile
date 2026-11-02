# Makefile for test plugin

CC = gcc
CFLAGS = -fPIC -shared -I../../../../lib -I../../../../drivers -I..
LDFLAGS = -lpthread

TARGET = test_plugin.so
SOURCE = test_plugin.c
PLUGIN_LOGGER = ../plugin_logger.c

LOADER_TARGET = test_plugin_loader
LOADER_SOURCE = test_plugin_loader.c

all: $(TARGET) $(LOADER_TARGET)

$(TARGET): $(SOURCE) $(PLUGIN_LOGGER)
	$(CC) $(CFLAGS) $(LDFLAGS) -o $(TARGET) $(SOURCE) $(PLUGIN_LOGGER)

$(LOADER_TARGET): $(LOADER_SOURCE)
	$(CC) -o $(LOADER_TARGET) $(LOADER_SOURCE) -ldl -lpthread

test: $(TARGET) $(LOADER_TARGET)
	./$(LOADER_TARGET)

clean:
	rm -f $(TARGET) $(LOADER_TARGET)

.PHONY: all clean test
