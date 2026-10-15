// Exercise the real output callback without opening a hardware device.
#include "../native/engine.cpp"
#include <iostream>

void check(bool ok,const char* message){if(!ok)throw std::runtime_error(message);}
int main(){
    try{
        Engine e;
        e.rate=48000;e.channels=2;e.volume=1;e.duration=30;e.origin=5;e.offset=5;e.limit=5+10.0/48000;
        float input[32];std::fill(std::begin(input),std::end(input),0.25f);
        e.push(reinterpret_cast<uint8_t*>(input),sizeof(input));
        float output[16];
        e.pull(output,sizeof(output));
        check(std::all_of(std::begin(output),std::end(output),[](float v){return v==0.25f;}),"first callback preserves samples");
        e.pull(output,sizeof(output));
        for(int i=0;i<16;i++)check(output[i]==(i<4?0.25f:0.f),"boundary callback must silence samples after cue end");
        auto consumed=e.played.load();
        e.pull(output,sizeof(output));
        check(std::all_of(std::begin(output),std::end(output),[](float v){return v==0;}),"subsequent callback must remain silent");
        check(e.played==consumed&&e.tail==80,"callback must not consume the next track");
        check(e.atLimit()&&e.underruns==0,"cue end must report ended without underruns");
        e.origin=5;e.offset=8;e.limit=15;e.played=0;
        check(e.span()==10&&e.position()==3,"resume position must use fixed cue origin");
        std::cout<<"PASS cue callback samples and coordinates\n";
        return 0;
    }catch(const std::exception& ex){std::cerr<<ex.what()<<'\n';return 1;}
}
