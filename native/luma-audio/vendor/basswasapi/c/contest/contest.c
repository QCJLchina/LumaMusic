/*
	WASAPI version of the BASS simple console player
	Copyright (c) 1999-2023 Un4seen Developments Ltd.
*/

#include <stdlib.h>
#include <stdio.h>
#include <conio.h>
#include "basswasapi.h"
#include "bassmix.h"
#include "bass.h"

HSTREAM mixer;

// display error messages
void Error(const char *text)
{
	printf("Error(%d): %s\n", BASS_ErrorGetCode(), text);
	BASS_WASAPI_Free();
	BASS_Free();
	exit(0);
}

// WASAPI function
DWORD CALLBACK WasapiProc(void *buffer, DWORD length, void *user)
{
	return BASS_ChannelGetData(mixer, buffer, length);
}

void ListDevices()
{
	BASS_WASAPI_DEVICEINFO di;
	int a;
	for (a = 0; BASS_WASAPI_GetDeviceInfo(a, &di); a++) {
		if ((di.flags & BASS_DEVICE_ENABLED) && !(di.flags & BASS_DEVICE_INPUT)) // enabled output device
			printf("dev %d: %s\n", a, di.name);
	}
}

int main(int argc, char **argv)
{
	const char *formats[5] = { "32 bit float", "8 bit", "16 bit", "24 bit", "32 bit" };
	DWORD chan, opos;
	QWORD pos;
	double secs;
	int a, filep, device = -1;
	BASS_CHANNELINFO info;
	BASS_WASAPI_DEVICEINFO dinfo;
	DWORD flags = BASS_WASAPI_AUTOFORMAT | BASS_WASAPI_BUFFER; // default initialization flags

	printf("BASSWASAPI simple console player\n"
		"--------------------------------\n");

	// check the correct BASS was loaded
	if (HIWORD(BASS_GetVersion()) != BASSVERSION) {
		printf("An incorrect version of BASS was loaded");
		return;
	}

	for (filep = 1; filep < argc; filep++) {
		if (!strcmp(argv[filep], "-l")) {
			ListDevices();
			return;
		} else if (!strcmp(argv[filep], "-d") && filep + 1 < argc) device = atoi(argv[++filep]);
		else if (!strcmp(argv[filep], "-x")) flags |= BASS_WASAPI_EXCLUSIVE;
		else if (!strcmp(argv[filep], "-e")) flags |= BASS_WASAPI_EVENT;
		else if (!strcmp(argv[filep], "-r")) flags &= ~BASS_WASAPI_AUTOFORMAT;
		else break;
	}
	if (filep == argc) {
		printf("\tusage: contest [-l] [-d #] [-x] [-e] [-r] <file>\n"
			"\t-l = list devices\n"
			"\t-d = device number\n"
			"\t-x = exclusive mode, else shared mode\n"
			"\t-e = event-driven buffering\n"
			"\t-r = WASAPI resampling, else BASSmix\n");
		return;
	}

	if (device == -1) { // find the default output device and get its "mix" format
		for (device == -1, a = 0; BASS_WASAPI_GetDeviceInfo(a, &dinfo); a++) {
			if ((dinfo.flags & (BASS_DEVICE_DEFAULT | BASS_DEVICE_LOOPBACK | BASS_DEVICE_INPUT)) == BASS_DEVICE_DEFAULT) {
				device = a;
				break;
			}
		}
		if (device == -1) Error("Can't find an output device");
	} else {
		if (!BASS_WASAPI_GetDeviceInfo(device, &dinfo) || (dinfo.flags & BASS_DEVICE_INPUT))
			Error("Invalid device number");
	}

	// not playing anything via BASS, so don't need an update thread
	BASS_SetConfig(BASS_CONFIG_UPDATETHREADS, 0);
	// setup BASS - "no sound" device with the "mix" sample rate (default for MOD music)
	BASS_Init(0, dinfo.mixfreq, 0, 0, NULL);

	if (strstr(argv[filep], "://")) {
		// try streaming the URL
		chan = BASS_StreamCreateURL(argv[filep], 0, BASS_SAMPLE_FLOAT | BASS_SAMPLE_LOOP | BASS_STREAM_DECODE, 0, 0);
	} else {
		// try streaming the file
		chan = BASS_StreamCreateFile(0, argv[filep], 0, 0, BASS_SAMPLE_FLOAT | BASS_SAMPLE_LOOP | BASS_STREAM_DECODE);
		if (!chan && BASS_ErrorGetCode() == BASS_ERROR_FILEFORM) {
			// try MOD music formats
			chan = BASS_MusicLoad(0, argv[filep], 0, 0, BASS_SAMPLE_FLOAT | BASS_SAMPLE_LOOP | BASS_MUSIC_RAMPS | BASS_MUSIC_PRESCAN | BASS_MUSIC_DECODE, 0);
		}
	}
	if (!chan) Error("Can't play the file");

	BASS_ChannelGetInfo(chan, &info);
	printf("ctype: %x\n", info.ctype);
	if (HIWORD(info.ctype) != 2) { // not MOD music
		if (info.origres)
			printf("format: %u Hz, %d chan, %d bit\n", info.freq, info.chans, LOWORD(info.origres));
		else
			printf("format: %u Hz, %d chan\n", info.freq, info.chans);
	}
	pos = BASS_ChannelGetLength(chan, BASS_POS_BYTE);
	if (pos != -1) {
		secs = BASS_ChannelBytes2Seconds(chan, pos);
		if (HIWORD(info.ctype) == 2)
			printf("length: %u:%02u (%llu samples), %u orders\n", (int)secs / 60, (int)secs % 60, (long long)(secs * info.freq), (DWORD)BASS_ChannelGetLength(chan, BASS_POS_MUSIC_ORDER));
		else
			printf("length: %u:%02u (%llu samples)\n", (int)secs / 60, (int)secs % 60, (long long)(secs * info.freq));
	} else if (HIWORD(info.ctype) == 2)
		printf("length: %u orders\n", (DWORD)BASS_ChannelGetLength(chan, BASS_POS_MUSIC_ORDER));

	{ // setup output
		float buflen = ((flags & (BASS_WASAPI_EXCLUSIVE | BASS_WASAPI_EVENT)) == (BASS_WASAPI_EXCLUSIVE | BASS_WASAPI_EVENT) ? 0.1 : 0.4); // smaller buffer with event-driven exclusive mode
		BASS_WASAPI_INFO wi;
		// initialize the WASAPI device
		if (!BASS_WASAPI_Init(device, info.freq, info.chans, flags, buflen, 0.05, WasapiProc, NULL)) {
			// failed, try falling back to shared mode
			if (!(flags & BASS_WASAPI_EXCLUSIVE) || !BASS_WASAPI_Init(device, info.freq, info.chans, flags & ~BASS_WASAPI_EXCLUSIVE, buflen, 0.05, WasapiProc, NULL))
				Error("Can't initialize device");
		}
		// get the output details
		BASS_WASAPI_GetInfo(&wi);
		printf("output: %s%s mode, %d Hz, %d chan, %s\n",
			wi.initflags & BASS_WASAPI_EVENT ? "event-driven " : "", wi.initflags & BASS_WASAPI_EXCLUSIVE ? "exclusive" : "shared", wi.freq, wi.chans, formats[wi.format]);
		// create a mixer with the same sample format and enable GetPositionEx
		mixer = BASS_Mixer_StreamCreate(wi.freq, wi.chans, BASS_SAMPLE_FLOAT | BASS_STREAM_DECODE | BASS_MIXER_POSEX);
		// add the source to the mixer (downmix if necessary)
		BASS_Mixer_StreamAddChannel(mixer, chan, BASS_MIXER_DOWNMIX);
		// start it
		if (!BASS_WASAPI_Start())
			Error("Can't start output");
	}

	while (!_kbhit() && BASS_ChannelIsActive(mixer)) {
		// display some stuff and wait a bit
		BASS_WASAPI_Lock(TRUE); // prevent processing mid-calculation
		a = BASS_WASAPI_GetData(NULL, BASS_DATA_AVAILABLE); // get amount of buffered data
		pos = BASS_Mixer_ChannelGetPositionEx(chan, BASS_POS_BYTE, a); // get source position at that point
		if (HIWORD(info.ctype) == 2)
			opos = BASS_Mixer_ChannelGetPositionEx(chan, BASS_POS_MUSIC_ORDER, a); // get the "order" position too
		BASS_WASAPI_Lock(FALSE);
		secs = BASS_ChannelBytes2Seconds(chan, pos);
		printf(" %u:%02u (%08lld)", (int)secs / 60, (int)secs % 60, (long long)(secs * info.freq));
		if (HIWORD(info.ctype) == 2)
			printf(" | %03u:%03u", LOWORD(opos), HIWORD(opos));
		printf(" | L ");
		{
			DWORD level = BASS_WASAPI_GetLevel();
			for (a = 27204; a > 200; a = a * 2 / 3) putchar(LOWORD(level) >= a ? '*' : '-');
			putchar(' ');
			for (a = 210; a < 32768; a = a * 3 / 2) putchar(HIWORD(level) >= a ? '*' : '-');
		}
		printf(" R | cpu %.2f%%  \r", BASS_WASAPI_GetCPU());
		fflush(stdout);
		Sleep(50);
	}
	printf("                                                                             \n");

	BASS_WASAPI_Free();
	BASS_Free();
	return 0;
}
