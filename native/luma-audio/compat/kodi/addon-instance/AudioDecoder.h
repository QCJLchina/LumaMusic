#pragma once
#include <string>
#include <vector>
#include <cstdint>
namespace kodi::addon {
class AudioDecoderInfoTag {
public:
    std::string title, artist, album, albumArtist, genre, date, comment, composer;
    int track = 0, disc = 0, discs = 0;
    void SetTitle(const std::string& v) {title=v;} void SetArtist(const std::string& v){artist=v;}
    void SetAlbum(const std::string& v){album=v;} void SetAlbumArtist(const std::string& v){albumArtist=v;}
    void SetGenre(const std::string& v){genre=v;} void SetReleaseDate(const std::string& v){date=v;}
    void SetComment(const std::string& v){comment=v;} void SetComposer(const std::string& v){composer=v;}
    void SetTrack(int v){track=v;} void SetDisc(int v){disc=v;} void SetDiscTotal(int v){discs=v;}
    int GetTrack() const {return track;}
};
}
