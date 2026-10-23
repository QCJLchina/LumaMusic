/*
	BASSmix multiple output example
	Copyright (c) 2009-2021 Un4seen Developments Ltd.
*/

#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <commctrl.h>
#include "bass.h"
#include "bassmix.h"

HWND win;
OPENFILENAME ofn;

DWORD outdev[2] = { 1, 0 };	// output devices
DWORD source;		// source channel
HSTREAM split[2];	// output splitter streams

// display error messages
void Error(const char *es)
{
	char mes[200];
	sprintf(mes, "%s\n(error code: %d)", es, BASS_ErrorGetCode());
	MessageBox(win, mes, 0, 0);
}

#define MESS(id, m, w, l) SendDlgItemMessage(win, id, m,(WPARAM)(w), (LPARAM)(l))

BOOL CreateClone()
{
	// set the device to create 2nd splitter stream on, and then create it
	BASS_SetDevice(outdev[1]);
	if (!(split[1] = BASS_Split_StreamCreate(source, 0, NULL))) {
		Error("Can't create splitter");
		return FALSE;
	}
	BASS_ChannelSetLink(split[0], split[1]); // link the splitters to play together
	return TRUE;
}

INT_PTR CALLBACK DialogProc(HWND h, UINT m, WPARAM w, LPARAM l)
{
	switch (m) {
		case WM_COMMAND:
			switch (LOWORD(w)) {
				case IDCANCEL:
					EndDialog(h, 0);
					break;

				case 10: // open a file to play
					{
						char file[MAX_PATH] = "";
						ofn.lpstrFile = file;
						if (GetOpenFileName(&ofn)) {
							BASS_ChannelFree(source); // free old channel (splitters automatically freed too)
							if (!(source = BASS_StreamCreateFile(0, file, 0, 0, BASS_STREAM_DECODE | BASS_SAMPLE_LOOP | BASS_SAMPLE_FLOAT))
								&& !(source = BASS_MusicLoad(0, file, 0, 0, BASS_MUSIC_DECODE | BASS_MUSIC_PRESCAN | BASS_MUSIC_POSRESET | BASS_MUSIC_RAMPS | BASS_SAMPLE_LOOP | BASS_SAMPLE_FLOAT, 1))) {
								MESS(10, WM_SETTEXT, 0, "Open file...");
								Error("Can't play the file");
								break;
							}
							// disassociate source from any device so it isn't freed when changing device
							BASS_ChannelSetDevice(source, BASS_NODEVICE);
							// set the device to create 1st splitter stream on, and then create it
							BASS_SetDevice(outdev[0]);
							if (!(split[0] = BASS_Split_StreamCreate(source, 0, NULL))) {
								BASS_ChannelFree(source);
								source = 0;
								MESS(10, WM_SETTEXT, 0, "Open file...");
								Error("Can't create splitter");
							}
							if (MESS(11, BM_GETCHECK, 0, 0))
								CreateClone(); // create a clone
							else
								split[1] = 0; // no clone
							MESS(10, WM_SETTEXT, 0, strrchr(file, '\\') + 1);
							{ // update scroller range
								QWORD len = BASS_ChannelGetLength(source, BASS_POS_BYTE);
								if (len == -1) len = 0; // unknown length
								MESS(14, TBM_SETRANGEMAX, 1, BASS_ChannelBytes2Seconds(source, len) * 1000);
							}
							BASS_ChannelPlay(split[0], FALSE); // start playback
						}
					}
					break;

				case 11:
					if (!split[0]) break;
					if (MESS(11, BM_GETCHECK, 0, 0)) { // create clone on device #2
						if (!split[1] && CreateClone()) {
							int offset;
							BASS_INFO info;
							BASS_GetInfo(&info);
							offset = BASS_Split_StreamGetAvailable(split[0]) // get the amount of data the 1st splitter has buffered
								+ BASS_ChannelGetData(split[0], NULL, BASS_DATA_AVAILABLE) // add the amount in its playback buffer
								- BASS_ChannelSeconds2Bytes(split[0], info.latency / 1000.0); // subtract the device's playback delay
							if (offset < 0) offset = 0; // just in case
							BASS_Split_StreamResetEx(split[1], offset); // set the new splitter that far back in the source buffer
							BASS_ChannelPlay(split[1], FALSE); // start the clone
						}
					} else { // remove clone on device #2
						BASS_ChannelFree(split[1]);
						split[1] = 0;
					}
					break;

				case 12: // device #1
				case 13: // device #2
					if (HIWORD(w) == CBN_SELCHANGE) { // device selection changed
						int sel = MESS(LOWORD(w), CB_GETCURSEL, 0, 0); // get the selection
						int devn = LOWORD(w) - 12;
						if (outdev[devn] == sel) break;
						if (!BASS_Init(sel, 44100, 0, win, NULL)) { // initialize new device
							Error("Can't initialize device");
							MESS(LOWORD(w), CB_SETCURSEL, outdev[devn], 0);
							break;
						}
						if (split[devn]) BASS_ChannelSetDevice(split[devn], sel); // move channel to new device
						BASS_SetDevice(outdev[devn]); // set context to old device
						BASS_Free(); // free it
						outdev[devn] = sel;
					}
					break;
			}
			break;

		case WM_HSCROLL:
			if (l && LOWORD(w) != SB_THUMBPOSITION && LOWORD(w) != SB_ENDSCROLL) { // set the position
				int pos = SendMessage((HWND)l, TBM_GETPOS, 0, 0);
				BASS_ChannelPause(split[0]); // pause splitter streams (so that resumption following seek can be synchronized)
				BASS_ChannelSetPosition(source, BASS_ChannelSeconds2Bytes(source, pos / 1000.0), BASS_POS_BYTE); // set source position
				BASS_Split_StreamReset(source); // reset buffers of all (both) the source's splitters
				BASS_ChannelPlay(split[0], FALSE); // resume playback
			}
			break;

		case WM_TIMER:
			if (source)
				MESS(14, TBM_SETPOS, 1, BASS_ChannelBytes2Seconds(split[0], BASS_ChannelGetPosition(split[0], BASS_POS_BYTE)) * 1000); // update position (using 1st splitter)
			break;

		case WM_INITDIALOG:
			win = h;
			memset(&ofn, 0, sizeof(ofn));
			ofn.lStructSize = sizeof(ofn);
			ofn.hwndOwner = h;
			ofn.nMaxFile = MAX_PATH;
			ofn.Flags = OFN_HIDEREADONLY | OFN_EXPLORER;
			ofn.lpstrFilter = "Playable files\0*.mo3;*.xm;*.mod;*.s3m;*.it;*.mtm;*.umx;*.mp3;*.mp2;*.mp1;*.ogg;*.wav;*.aif\0All files\0*.*\0\0";
			{ // get list of output devices
				int c;
				BASS_DEVICEINFO di;
				for (c = 0; BASS_GetDeviceInfo(c, &di); c++) {
					MESS(12, CB_ADDSTRING, 0, di.name);
					if (c == outdev[0]) MESS(12, CB_SETCURSEL, c, 0);
					MESS(13, CB_ADDSTRING, 0, di.name);
					if (c == outdev[1]) MESS(13, CB_SETCURSEL, c, 0);
				}
			}
			// initialize the output devices
			if (!BASS_Init(outdev[0], 44100, 0, win, NULL) || !BASS_Init(outdev[1], 44100, 0, win, NULL)) {
				Error("Can't initialize device");
				EndDialog(h, 0);
			}
			MESS(14, TBM_SETLINESIZE, 0, 1000);
			SetTimer(h, 0, 100, 0); // timer to update the position display
			return 1;

		case WM_DESTROY:
			// release both devices
			BASS_SetDevice(outdev[0]);
			BASS_Free();
			BASS_SetDevice(outdev[1]);
			BASS_Free();
			// free source separately because it isn't associated with a device
			BASS_ChannelFree(source);
			break;
	}

	return 0;
}

int PASCAL WinMain(HINSTANCE hInstance, HINSTANCE hPrevInstance, LPSTR lpCmdLine, int nCmdShow)
{
	// check the correct BASS was loaded
	if (HIWORD(BASS_GetVersion()) != BASSVERSION) {
		MessageBox(0, "An incorrect version of BASS.DLL was loaded", 0, MB_ICONERROR);
		return 0;
	}

	{
		INITCOMMONCONTROLSEX cc = { sizeof(cc), ICC_BAR_CLASSES };
		InitCommonControlsEx(&cc);
	}

	DialogBox(hInstance, MAKEINTRESOURCE(1000), 0, DialogProc);

	return 0;
}

