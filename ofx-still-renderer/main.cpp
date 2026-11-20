#include "SpektraProfileCurves.h"
#include "SpektraVulkanRenderer.h"

#include <algorithm>
#include <array>
#include <charconv>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <emmintrin.h>
#include <fcntl.h>
#include <iomanip>
#include <io.h>
#include <iostream>
#include <iterator>
#include <limits>
#include <locale>
#include <optional>
#include <sstream>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

namespace {

struct RenderCanceled {};
struct RenderDeviceLost : std::runtime_error {
    using std::runtime_error::runtime_error;
};

enum class WorkerStage : uint8_t { SourceAccepted = 1, Preparing, Vulkan, Verifying, Sending };
void writeProgress(std::optional<uint64_t> request, WorkerStage stage);


class RequestCancellation final {
public:
    RequestCancellation() {
        char name[128]{};
        size_t length = 0;
        if (getenv_s(&length, name, sizeof(name), "SPEKTRAFILM_REQUEST_CANCEL_EVENT") == 0 && length > 1)
            signal_ = OpenEventA(SYNCHRONIZE, FALSE, name);
    }
    ~RequestCancellation() { if (signal_) CloseHandle(signal_); }
    RequestCancellation(const RequestCancellation &) = delete;
    RequestCancellation &operator=(const RequestCancellation &) = delete;
    bool available() const noexcept { return signal_ != nullptr; }
    bool requested() const noexcept { return signal_ && WaitForSingleObject(signal_, 0) == WAIT_OBJECT_0; }
    void check() const { if (requested()) throw RenderCanceled{}; }
    static bool poll(const void *stop) noexcept { return static_cast<const RequestCancellation *>(stop)->requested(); }

private:
    HANDLE signal_ = nullptr;
};

using namespace spektrafilm;

constexpr int kProtocolVersion = 2;

struct NumericOption {
    std::string_view name;
    float RenderParams::*member;
    float min;
    float max;
};

constexpr NumericOption kNumericOptions[] = {
    {"film-exposure", &RenderParams::filmExposureEv, -8.0f, 8.0f},
    {"print-exposure", &RenderParams::printExposureEv, -5.0f, 5.0f},
    {"film-gamma", &RenderParams::filmGamma, 0.1f, 2.0f},
    {"print-gamma", &RenderParams::printGamma, 0.1f, 2.0f},
    {"film-push-pull", &RenderParams::filmPushPullStops, -2.0f, 2.0f},
    {"print-push-pull", &RenderParams::printPushPullStops, -2.0f, 2.0f},
    {"negative-bleach-bypass", &RenderParams::negativeBleachBypassAmount, 0.0f, 1.0f},
    {"print-bleach-bypass", &RenderParams::printBleachBypassAmount, 0.0f, 1.0f},
    {"negative-leuco-cyan", &RenderParams::negativeLeucoCyanCoupling, 0.0f, 2.0f},
    {"print-shadow-shape", &RenderParams::printShadowShape, -1.0f, 1.0f},
    {"print-highlight-shape", &RenderParams::printHighlightShape, -1.0f, 1.0f},
    {"cyan-filter", &RenderParams::filterC, 0.0f, 120.0f},
    {"magenta-filter", &RenderParams::filterMShift, -60.0f, 60.0f},
    {"yellow-filter", &RenderParams::filterYShift, -60.0f, 60.0f},
    {"preflash", &RenderParams::preflashExposure, 0.0f, 1.0f},
    {"preflash-magenta", &RenderParams::preflashMFilterShift, -60.0f, 60.0f},
    {"preflash-yellow", &RenderParams::preflashYFilterShift, -60.0f, 60.0f},
    {"uv-cutoff", &RenderParams::cameraUvCutNm, 380.0f, 450.0f},
    {"ir-cutoff", &RenderParams::cameraIrCutNm, 600.0f, 780.0f},
    {"enlarger-scale", &RenderParams::enlargerScale, 1.0f, 32.0f},
    {"enlarger-x", &RenderParams::enlargerOffsetXPercent, -100.0f, 100.0f},
    {"enlarger-y", &RenderParams::enlargerOffsetYPercent, -100.0f, 100.0f},
    {"dir-amount", &RenderParams::dirCouplersAmount, 0.0f, 2.0f},
    {"dir-diffusion", &RenderParams::dirCouplersDiffusionUm, 0.0f, 100.0f},
    {"dir-tail-size", &RenderParams::dirCouplersDiffusionTailUm, 0.0f, 1000.0f},
    {"dir-tail-weight", &RenderParams::dirCouplersDiffusionTailWeight, 0.0f, 1.0f},
    {"dir-same-layer", &RenderParams::dirCouplersInhibitionSameLayer, 0.0f, 2.0f},
    {"dir-interlayer", &RenderParams::dirCouplersInhibitionInterlayer, 0.0f, 2.0f},
    {"dir-gamma-r", &RenderParams::dirCouplersGammaSameLayerR, 0.0f, 1.0f},
    {"dir-gamma-g", &RenderParams::dirCouplersGammaSameLayerG, 0.0f, 1.0f},
    {"dir-gamma-b", &RenderParams::dirCouplersGammaSameLayerB, 0.0f, 1.0f},
    {"dir-r-to-g", &RenderParams::dirCouplersGammaRToG, 0.0f, 1.0f},
    {"dir-r-to-b", &RenderParams::dirCouplersGammaRToB, 0.0f, 1.0f},
    {"dir-g-to-r", &RenderParams::dirCouplersGammaGToR, 0.0f, 1.0f},
    {"dir-g-to-b", &RenderParams::dirCouplersGammaGToB, 0.0f, 1.0f},
    {"dir-b-to-r", &RenderParams::dirCouplersGammaBToR, 0.0f, 1.0f},
    {"dir-b-to-g", &RenderParams::dirCouplersGammaBToG, 0.0f, 1.0f},
    {"scatter-amount", &RenderParams::scatterAmount, 0.0f, 2.0f},
    {"scatter-scale", &RenderParams::scatterScale, 0.0f, 4.0f},
    {"halation-amount", &RenderParams::halationAmount, 0.0f, 4.0f},
    {"halation-scale", &RenderParams::halationScale, 0.0f, 4.0f},
    {"halation-boost", &RenderParams::halationBoostEv, 0.0f, 20.0f},
    {"halation-boost-range", &RenderParams::halationBoostRange, 0.0f, 1.0f},
    {"halation-protect", &RenderParams::halationProtectEv, 0.0f, 10.0f},
    {"halation-red", &RenderParams::halationStrengthR, 0.0f, 1.0f},
    {"halation-green", &RenderParams::halationStrengthG, 0.0f, 1.0f},
    {"halation-blue", &RenderParams::halationStrengthB, 0.0f, 1.0f},
    {"camera-diffusion-strength", &RenderParams::cameraDiffusionStrength, 0.0f, 2.0f},
    {"camera-diffusion-scale", &RenderParams::cameraDiffusionSpatialScale, 0.0f, 4.0f},
    {"camera-diffusion-warmth", &RenderParams::cameraDiffusionHaloWarmth, -1.5f, 1.5f},
    {"camera-diffusion-core-intensity", &RenderParams::cameraDiffusionCoreIntensity, 0.0f, 4.0f},
    {"camera-diffusion-core-size", &RenderParams::cameraDiffusionCoreSize, 0.1f, 4.0f},
    {"camera-diffusion-halo-intensity", &RenderParams::cameraDiffusionHaloIntensity, 0.0f, 4.0f},
    {"camera-diffusion-halo-size", &RenderParams::cameraDiffusionHaloSize, 0.1f, 4.0f},
    {"camera-diffusion-bloom-intensity", &RenderParams::cameraDiffusionBloomIntensity, 0.0f, 4.0f},
    {"camera-diffusion-bloom-size", &RenderParams::cameraDiffusionBloomSize, 0.1f, 4.0f},
    {"print-diffusion-strength", &RenderParams::printDiffusionStrength, 0.0f, 2.0f},
    {"print-diffusion-scale", &RenderParams::printDiffusionSpatialScale, 0.0f, 4.0f},
    {"print-diffusion-warmth", &RenderParams::printDiffusionHaloWarmth, -1.5f, 1.5f},
    {"print-diffusion-core-intensity", &RenderParams::printDiffusionCoreIntensity, 0.0f, 4.0f},
    {"print-diffusion-core-size", &RenderParams::printDiffusionCoreSize, 0.1f, 4.0f},
    {"print-diffusion-halo-intensity", &RenderParams::printDiffusionHaloIntensity, 0.0f, 4.0f},
    {"print-diffusion-halo-size", &RenderParams::printDiffusionHaloSize, 0.1f, 4.0f},
    {"print-diffusion-bloom-intensity", &RenderParams::printDiffusionBloomIntensity, 0.0f, 4.0f},
    {"print-diffusion-bloom-size", &RenderParams::printDiffusionBloomSize, 0.1f, 4.0f},
    {"grain-amount", &RenderParams::grainAmount, 0.0f, 2.0f},
    {"grain-saturation", &RenderParams::grainSaturation, 0.0f, 1.0f},
    {"grain-particle-area", &RenderParams::grainParticleAreaUm2, 0.01f, 5.0f},
    {"grain-final-blur", &RenderParams::grainFinalBlurUm, 0.0f, 25.0f},
    {"grain-dye-cloud-blur", &RenderParams::grainBlurDyeCloudsUm, 0.0f, 10.0f},
    {"grain-microstructure", &RenderParams::grainMicroStructureScale, 0.0f, 4.0f},
    {"grain-microstructure-sigma", &RenderParams::grainMicroStructureSigmaNm, 0.0f, 500.0f},
    {"grain-particle-scale-r", &RenderParams::grainParticleScaleR, 0.01f, 20.0f},
    {"grain-density-min-r", &RenderParams::grainDensityMinR, 0.0f, 1.0f},
    {"grain-uniformity-r", &RenderParams::grainUniformityR, 0.0f, 1.0f},
    {"grain-synthesis-radius-scale-r", &RenderParams::grainSynthesisRadiusScaleR, 0.01f, 20.0f},
    {"grain-particle-scale-g", &RenderParams::grainParticleScaleG, 0.01f, 20.0f},
    {"grain-density-min-g", &RenderParams::grainDensityMinG, 0.0f, 1.0f},
    {"grain-uniformity-g", &RenderParams::grainUniformityG, 0.0f, 1.0f},
    {"grain-synthesis-radius-scale-g", &RenderParams::grainSynthesisRadiusScaleG, 0.01f, 20.0f},
    {"grain-particle-scale-b", &RenderParams::grainParticleScaleB, 0.01f, 20.0f},
    {"grain-density-min-b", &RenderParams::grainDensityMinB, 0.0f, 1.0f},
    {"grain-uniformity-b", &RenderParams::grainUniformityB, 0.0f, 1.0f},
    {"grain-synthesis-radius-scale-b", &RenderParams::grainSynthesisRadiusScaleB, 0.01f, 20.0f},
    {"grain-layer-scale-0", &RenderParams::grainParticleScaleLayer0, 0.01f, 20.0f},
    {"grain-synthesis-layer-scale-0", &RenderParams::grainSynthesisLayerScale0, 0.01f, 20.0f},
    {"grain-layer-scale-1", &RenderParams::grainParticleScaleLayer1, 0.01f, 20.0f},
    {"grain-synthesis-layer-scale-1", &RenderParams::grainSynthesisLayerScale1, 0.01f, 20.0f},
    {"grain-layer-scale-2", &RenderParams::grainParticleScaleLayer2, 0.01f, 20.0f},
    {"grain-synthesis-layer-scale-2", &RenderParams::grainSynthesisLayerScale2, 0.01f, 20.0f},
    {"grain-synthesis-size", &RenderParams::grainSynthesisSize, 0.25f, 4.0f},
    {"grain-synthesis-amount", &RenderParams::grainSynthesisAmount, 0.0f, 3.0f},
    {"grain-synthesis-sharpness", &RenderParams::grainSynthesisSharpness, 0.25f, 4.0f},
    {"grain-synthesis-quality", &RenderParams::grainSynthesisQuality, 0.25f, 4.0f},
    {"grain-synthesis-radius", &RenderParams::grainSynthesisMeanRadiusUm, 0.05f, 10.0f},
    {"grain-synthesis-radius-sd", &RenderParams::grainSynthesisRadiusStdDevRatio, 0.0f, 1.0f},
    {"grain-synthesis-observation", &RenderParams::grainSynthesisObservationSigmaUm, 0.0f, 20.0f},
    {"grain-synthesis-cell", &RenderParams::grainSynthesisCellSizeRatio, 0.25f, 2.0f},
    {"grain-synthesis-radius-quantile", &RenderParams::grainSynthesisMaxRadiusQuantile, 0.95f, 0.9999f},
    {"grain-synthesis-epsilon", &RenderParams::grainSynthesisCoverageEpsilon, 0.000001f, 0.01f},
    {"scanner-white-level", &RenderParams::scannerWhiteLevel, 0.0f, 1.0f},
    {"scanner-black-level", &RenderParams::scannerBlackLevel, 0.0f, 1.0f},
    {"scanner-glare", &RenderParams::glarePercent, 0.0f, 0.2f},
    {"scanner-glare-roughness", &RenderParams::glareRoughness, 0.0f, 4.0f},
    {"scanner-glare-blur", &RenderParams::glareBlur, 0.0f, 32.0f},
    {"scanner-mtf", &RenderParams::scannerMtf50LpMm, 0.0f, 300.0f},
    {"scanner-unsharp-radius", &RenderParams::scannerUnsharpRadiusUm, 0.0f, 100.0f},
    {"scanner-unsharp-amount", &RenderParams::scannerUnsharpAmount, 0.0f, 4.0f},
};

struct BooleanOption {
    std::string_view name;
    bool RenderParams::*member;
};

constexpr BooleanOption kBooleanOptions[] = {
    {"color-adaptation", &RenderParams::colorAdaptation},
    {"color-input-compression", &RenderParams::colorAdaptationInputCompression},
    {"color-curve-smoothing", &RenderParams::colorAdaptationCurveSmoothing},
    {"color-output-lightness-compression", &RenderParams::colorAdaptationOutputLightnessCompression},
    {"color-output-chroma-compression", &RenderParams::colorAdaptationOutputChromaCompression},
    {"uv-enabled", &RenderParams::cameraUvFilterEnabled},
    {"ir-enabled", &RenderParams::cameraIrFilterEnabled},
    {"auto-exposure", &RenderParams::autoExposure},
    {"grain-sublayers", &RenderParams::grainSublayersEnabled},
    {"grain-synthesis-layered", &RenderParams::grainSynthesisLayered},
    {"scanner-enabled", &RenderParams::scannerEnabled},
    {"scanner-white", &RenderParams::scannerWhiteCorrection},
    {"scanner-black", &RenderParams::scannerBlackCorrection},
};

struct IntegerOption {
    std::string_view name;
    int32_t RenderParams::*member;
    int min;
    int max;
};

constexpr IntegerOption kIntegerOptions[] = {
    {"grain-sublayer-count", &RenderParams::grainSubLayerCount, 1, 8},
    {"grain-synthesis-samples", &RenderParams::grainSynthesisSamples, 1, 2048},
    {"grain-synthesis-cell-cap", &RenderParams::grainSynthesisMaxGrainsPerCell, 1, 128},
};
constexpr int kMaximumGrainSeed = std::numeric_limits<int32_t>::max();

float number(const std::string &value, float low, float high) {
    std::size_t used = 0;
    const float result = std::stof(value, &used);
    if (used != value.size() || !std::isfinite(result) || result < low || result > high)
        throw std::invalid_argument("Value outside the permitted range: " + value);
    return result;
}

int integer(const std::string &value, int low, int high) {
    std::size_t used = 0;
    const int result = std::stoi(value, &used);
    if (used != value.size() || result < low || result > high)
        throw std::invalid_argument("Invalid integer: " + value);
    return result;
}

bool boolean(const std::string &value) {
    if (value == "true" || value == "1") return true;
    if (value == "false" || value == "0") return false;
    throw std::invalid_argument("Expected true or false: " + value);
}

template <typename Choice>
struct Option {
    std::string_view name;
    Choice value;
};

template <typename Choice, std::size_t N>
Choice select(const std::string &value, const Option<Choice> (&choices)[N]) {
    for (const auto &option : choices)
        if (value == option.name) return option.value;
    throw std::invalid_argument("Unknown option value: " + value);
}

constexpr Option<ColorSpace> kInputColorSpaces[] = {
    {"srgb", ColorSpace::Srgb}, {"linear_rec709", ColorSpace::LinearRec709},
    {"linear_rec2020", ColorSpace::LinearRec2020}, {"prophoto", ColorSpace::ProPhotoRgb},
    {"adobe_rgb", ColorSpace::AdobeRgb1998}, {"display_p3", ColorSpace::DisplayP3},
};

constexpr Option<ColorSpace> kOutputColorSpaces[] = {
    {"srgb", ColorSpace::Srgb}, {"rec709_gamma24", ColorSpace::Rec709Gamma24},
    {"linear_rec709", ColorSpace::LinearRec709},
};

constexpr Option<RgbToRawMethod> kUpsampling[] = {
    {"hanatos2026", RgbToRawMethod::Hanatos2026}, {"hanatos2025", RgbToRawMethod::Hanatos2025},
    {"mallett2019", RgbToRawMethod::Mallett2019},
};

constexpr Option<DiffusionFilterFamily> kDiffusion[] = {
    {"glimmerglass", DiffusionFilterFamily::Glimmerglass}, {"black_pro_mist", DiffusionFilterFamily::BlackProMist},
    {"pro_mist", DiffusionFilterFamily::ProMist}, {"cine_bloom", DiffusionFilterFamily::CineBloom},
    {"cinebloom", DiffusionFilterFamily::CineBloom},
};

constexpr Option<GrainModel> kGrain[] = {
    {"preview", GrainModel::Preview}, {"production", GrainModel::Production},
    {"synthesis", GrainModel::GrainSynthesis},
};

constexpr Option<FilmFormat> kFormats[] = {
    {"8", FilmFormat::Standard8}, {"super8", FilmFormat::Super8},
    {"16", FilmFormat::Standard16}, {"super16", FilmFormat::Super16},
    {"35", FilmFormat::Standard35}, {"super35", FilmFormat::Super35},
    {"65", FilmFormat::Standard65}, {"70", FilmFormat::Imax70},
};

constexpr Option<ProcessMode> kProcesses[] = {
    {"print", ProcessMode::PrintSimulation}, {"scan", ProcessMode::ScanNegative},
    {"scan_inverted", ProcessMode::ScanNegative}, {"process_negative", ProcessMode::ProcessNegative},
};

constexpr Option<PushPullMode> kPushPullModes[] = {
    {"standard", PushPullMode::Standard}, {"experimental", PushPullMode::Experimental},
};

constexpr Option<AutoExposureMethod> kMeteringMethods[] = {
    {"center_weighted", AutoExposureMethod::CenterWeighted}, {"median", AutoExposureMethod::Median},
};

int stockIndex(const std::string &id, bool film) {
    const auto count = film ? kSpektraFilmCount : kSpektraPaperCount;
    for (uint32_t index = 0; index < count; ++index) {
        const auto *profile = film ? filmProfileCurves(index) : paperProfileCurves(index);
        if (profile && profile->stock && id == profile->stock)
            return static_cast<int>(index);
    }
    throw std::invalid_argument("Unknown stock: " + id);
}

void setStockCalibration(RenderParams &p) {
    const auto *stock = filmProfileCurves(p.film);
    if (!stock) return;
    if (stock->halationStrength) {
        p.halationStrengthR = stock->halationStrength[0];
        p.halationStrengthG = stock->halationStrength[1];
        p.halationStrengthB = stock->halationStrength[2];
    }
    if (stock->dirGammaSameLayerRgb) {
        p.dirCouplersGammaSameLayerR = stock->dirGammaSameLayerRgb[0];
        p.dirCouplersGammaSameLayerG = stock->dirGammaSameLayerRgb[1];
        p.dirCouplersGammaSameLayerB = stock->dirGammaSameLayerRgb[2];
    }
    if (stock->dirGammaRToGb && stock->dirGammaGToRb && stock->dirGammaBToRg) {
        p.dirCouplersGammaRToG = stock->dirGammaRToGb[0];
        p.dirCouplersGammaRToB = stock->dirGammaRToGb[1];
        p.dirCouplersGammaGToR = stock->dirGammaGToRb[0];
        p.dirCouplersGammaGToB = stock->dirGammaGToRb[1];
        p.dirCouplersGammaBToR = stock->dirGammaBToRg[0];
        p.dirCouplersGammaBToG = stock->dirGammaBToRg[1];
    }
}

void configure(RenderParams &p, std::string_view key, const std::string &value) {
    for (const auto &option : kNumericOptions) {
        if (key == option.name) {
            p.*option.member = number(value, option.min, option.max);
            return;
        }
    }
    for (const auto &option : kBooleanOptions) {
        if (key == option.name) {
            p.*option.member = boolean(value);
            return;
        }
    }
    for (const auto &option : kIntegerOptions) {
        if (key == option.name) {
            p.*option.member = integer(value, option.min, option.max);
            return;
        }
    }
    if (key == "stock" || key == "paper") return;
    if (key == "input-color") p.inputColorSpace = select(value, kInputColorSpaces);
    else if (key == "output-color") p.outputColorSpace = select(value, kOutputColorSpaces);
    else if (key == "spectral-method") p.rgbToRawMethod = select(value, kUpsampling);
    else if (key == "mode") {
        p.process = select(value, kProcesses);
        p.scanNegativeInvert = value == "scan_inverted";
    } else if (key == "film-format") p.filmFormat = select(value, kFormats);
    else if (key == "film-push-pull-mode") p.filmPushPullMode = select(value, kPushPullModes);
    else if (key == "auto-exposure-meter") p.autoExposureMethod = select(value, kMeteringMethods);
    else if (key == "camera-diffusion-family") {
        p.cameraDiffusionEnabled = value != "off";
        if (p.cameraDiffusionEnabled) p.cameraDiffusionFamily = select(value, kDiffusion);
    } else if (key == "print-diffusion-family") {
        p.printDiffusionEnabled = value != "off";
        if (p.printDiffusionEnabled) p.printDiffusionFamily = select(value, kDiffusion);
    } else if (key == "grain-model") {
        p.grainEnabled = value != "off";
        if (p.grainEnabled) p.grainModel = select(value, kGrain);
    } else if (key == "grain-seed") p.grainSeed = integer(value, 0, kMaximumGrainSeed);
    else throw std::invalid_argument("Unknown setting: --" + std::string(key));
}

RenderParams readParameters(const std::vector<std::string> &args) {
    RenderParams params;
    params.film = kSpektraDefaultFilmIndex;
    params.paper = kSpektraDefaultPaperIndex;
    params.inputColorSpace = ColorSpace::Srgb;
    params.outputColorSpace = ColorSpace::Srgb;
    params.grainFinalBlurUm = 7.17f;
    params.halationAmount = 0;
    params.scatterAmount = 0;

    if (args.size() % 2 != 0)
        throw std::invalid_argument("Each setting needs a value.");
    for (std::size_t arg = 0; arg < args.size(); arg += 2) {
        const std::string_view name(args[arg]);
        if (name == "--stock") params.film = stockIndex(args[arg + 1], true);
        if (name == "--paper") params.paper = stockIndex(args[arg + 1], false);
    }
    setStockCalibration(params);
    for (std::size_t arg = 0; arg < args.size(); arg += 2) {
        const std::string_view name(args[arg]);
        if (name.rfind("--", 0) != 0)
            throw std::invalid_argument("Expected --setting name: " + std::string(name));
        configure(params, name.substr(2), args[arg + 1]);
    }
    params.halationEnabled = params.halationAmount > 0 || params.scatterAmount > 0;
    // OFX positions use a bottom-left origin. The preview buffer starts at the top.
    params.enlargerOffsetYPercent = -params.enlargerOffsetYPercent;
    if (std::abs(params.enlargerScale - 1.0f) <= 1.0e-6f) {
        params.enlargerOffsetXPercent = 0;
        params.enlargerOffsetYPercent = 0;
    }
    if (params.process == ProcessMode::ProcessNegative) {
        if (!kSpektraAcademyPrinterDensityEnabled || !academyPrinterDensityData())
            throw std::invalid_argument("Process Negative requires compiled Academy Printer Density calibration.");
        params.rgbToRawMethod = RgbToRawMethod::Hanatos2026;
        params.film = kSpektraDefaultFilmIndex;
        params.filmFormat = FilmFormat::Standard35;
        params.cameraUvFilterEnabled = false;
        params.cameraIrFilterEnabled = false;
        params.filmExposureEv = 0;
        params.autoExposure = false;
        params.filmPushPullMode = PushPullMode::Standard;
        params.filmPushPullStops = 0;
        params.negativeBleachBypassAmount = 0;
        params.negativeLeucoCyanCoupling = 1;
        params.filmGamma = 1;
        params.dirCouplersAmount = 0;
        params.grainEnabled = false;
        params.halationEnabled = false;
        params.cameraDiffusionEnabled = false;
        params.enlargerScale = 1;
        params.enlargerOffsetXPercent = 0;
        params.enlargerOffsetYPercent = 0;
    }
    return params;
}

template <typename Choice, std::size_t N>
void writeChoice(std::ostream &out, std::string_view key, const Option<Choice> (&options)[N], Choice selected,
    bool canDisable = false, bool disabled = false) {
    std::string_view defaultValue = "off";
    if (!disabled) {
        const auto found = std::find_if(std::begin(options), std::end(options), [&](const auto &option) {
            return option.value == selected;
        });
        if (found == std::end(options)) throw std::logic_error("Renderer default is not listed: " + std::string(key));
        defaultValue = found->name;
    }
    out << "{\"key\":" << std::quoted(key) << ",\"kind\":\"choice\",\"default\":" << std::quoted(defaultValue)
        << ",\"choices\":[";
    if (canDisable) out << "\"off\",";
    for (std::size_t index = 0; index < N; ++index) {
        if (index != 0) out << ',';
        out << std::quoted(options[index].name);
    }
    out << "]}";
}

void writeStockChoices(std::ostream &out, bool film, int32_t defaultIndex) {
    const auto *selected = film ? filmProfileCurves(defaultIndex) : paperProfileCurves(defaultIndex);
    if (!selected || !selected->stock) throw std::logic_error("Missing default renderer stock.");
    out << "{\"key\":\"" << (film ? "stock" : "paper") << "\",\"kind\":\"choice\",\"default\":"
        << std::quoted(selected->stock) << ",\"choices\":[";
    const auto count = film ? kSpektraFilmCount : kSpektraPaperCount;
    for (uint32_t index = 0; index < count; ++index) {
        const auto *profile = film ? filmProfileCurves(index) : paperProfileCurves(index);
        if (!profile || !profile->stock) throw std::logic_error("Missing compiled renderer stock.");
        if (index != 0) out << ',';
        out << std::quoted(profile->stock);
    }
    out << "]}";
}

void describeParameters() {
    const auto defaults = readParameters({});
    auto &out = std::cout;
    out.imbue(std::locale::classic());
    out << std::boolalpha << std::setprecision(std::numeric_limits<float>::max_digits10)
        << "{\"schemaVersion\":1,\"rendererProtocol\":" << kProtocolVersion
        << ",\"parameterContract\":\"" << SPEKTRA_PARAMETER_CONTRACT
        << "\",\"parameters\":[";
    for (const auto &option : kNumericOptions)
        out << "{\"key\":" << std::quoted(option.name) << ",\"kind\":\"number\",\"minimum\":" << option.min
            << ",\"maximum\":" << option.max << ",\"default\":" << defaults.*option.member << "},";
    for (const auto &option : kIntegerOptions)
        out << "{\"key\":" << std::quoted(option.name) << ",\"kind\":\"integer\",\"minimum\":" << option.min
            << ",\"maximum\":" << option.max << ",\"default\":" << defaults.*option.member << "},";
    out << "{\"key\":\"grain-seed\",\"kind\":\"integer\",\"minimum\":0,\"maximum\":" << kMaximumGrainSeed
        << ",\"default\":" << defaults.grainSeed << "},";
    for (const auto &option : kBooleanOptions)
        out << "{\"key\":" << std::quoted(option.name) << ",\"kind\":\"boolean\",\"default\":" << defaults.*option.member << "},";
    writeChoice(out, "input-color", kInputColorSpaces, defaults.inputColorSpace); out << ',';
    writeChoice(out, "output-color", kOutputColorSpaces, defaults.outputColorSpace); out << ',';
    writeChoice(out, "spectral-method", kUpsampling, defaults.rgbToRawMethod); out << ',';
    writeChoice(out, "mode", kProcesses, defaults.process); out << ',';
    writeChoice(out, "film-format", kFormats, defaults.filmFormat); out << ',';
    writeChoice(out, "film-push-pull-mode", kPushPullModes, defaults.filmPushPullMode); out << ',';
    writeChoice(out, "auto-exposure-meter", kMeteringMethods, defaults.autoExposureMethod); out << ',';
    writeChoice(out, "camera-diffusion-family", kDiffusion, defaults.cameraDiffusionFamily, true, !defaults.cameraDiffusionEnabled); out << ',';
    writeChoice(out, "print-diffusion-family", kDiffusion, defaults.printDiffusionFamily, true, !defaults.printDiffusionEnabled); out << ',';
    writeChoice(out, "grain-model", kGrain, defaults.grainModel, true, !defaults.grainEnabled); out << ',';
    writeStockChoices(out, true, defaults.film); out << ',';
    writeStockChoices(out, false, defaults.paper);

    out << "],\"scientific\":{\"academyPrinterDensity\":"
        << (kSpektraAcademyPrinterDensityEnabled && academyPrinterDensityData() != nullptr) << ",\"films\":[";
    for (uint32_t index = 0; index < kSpektraFilmCount; ++index) {
        const auto &profile = *filmProfileCurves(index);
        const auto *window = profile.hanatos2026WindowParams;
        const bool calibrated2026 = window && profile.wavelengths && profile.referenceIlluminantSpectrum
            && std::all_of(window, window + 4, [](float value) { return std::isfinite(value) && value > 0; });
        if (index != 0) out << ',';
        out << "{\"id\":" << std::quoted(profile.stock) << ",\"hanatos2025\":" << (profile.bandpassHanatos2025 != nullptr)
            << ",\"hanatos2026\":" << calibrated2026 << '}';
    }
    out << "]}}\n";
}

// The public Vulkan renderer uses this transform for film coordinates but does
// not resample the source. Match upstream's spektrafilm_enlarger_resample Metal pass.
std::vector<float> resampleEnlarger(const std::vector<float> &frame, int width, int height, const RenderParams &params,
                                    const RequestCancellation *stop) {
    std::vector<float> enlarged(frame.size());
    const float invScale = 1 / params.enlargerScale;
    const float offsetX = params.enlargerOffsetXPercent * 0.01f * invScale;
    const float offsetY = params.enlargerOffsetYPercent * 0.01f * invScale;
    const auto sourcePixel = [&](int x, int y) {
        const auto row = std::clamp(y, 0, height - 1);
        const auto column = std::clamp(x, 0, width - 1);
        return &frame[(row * width + column) * 4];
    };

    for (int y = 0; y < height; ++y) {
        if (stop) stop->check();
        const float v = 0.5f + ((y + 0.5f) / height - 0.5f) * invScale + offsetY;
        for (int x = 0; x < width; ++x) {
            const float u = 0.5f + ((x + 0.5f) / width - 0.5f) * invScale + offsetX;
            auto *pixel = &enlarged[(y * width + x) * 4];
            if (u < 0 || u > 1 || v < 0 || v > 1) {
                pixel[3] = 1;
                continue;
            }
            const float sourceX = u * width - 0.5f;
            const float sourceY = v * height - 0.5f;
            const int x0 = static_cast<int>(std::floor(sourceX));
            const int y0 = static_cast<int>(std::floor(sourceY));
            const float tx = sourceX - x0;
            const float ty = sourceY - y0;
            const auto *p00 = sourcePixel(x0, y0);
            const auto *p10 = sourcePixel(x0 + 1, y0);
            const auto *p01 = sourcePixel(x0, y0 + 1);
            const auto *p11 = sourcePixel(x0 + 1, y0 + 1);
            for (int channel = 0; channel < 4; ++channel) {
                const float top = p00[channel] + (p10[channel] - p00[channel]) * tx;
                const float bottom = p01[channel] + (p11[channel] - p01[channel]) * tx;
                pixel[channel] = top + (bottom - top) * ty;
            }
        }
    }
    return enlarged;
}

void enableBinaryPipes() {
    if (_setmode(_fileno(stdin), _O_BINARY) == -1 || _setmode(_fileno(stdout), _O_BINARY) == -1)
        throw std::runtime_error("Could not establish binary frame pipes.");
}

struct FrameSize {
    int width;
    int height;

    std::size_t componentCount() const {
        return static_cast<std::size_t>(width) * height * 4;
    }
};

struct InputFrame {
    FrameSize size;
    std::vector<float> pixels;
    uint64_t revision = 0;
};

struct EnlargerKey {
    uint64_t sourceRevision;
    float scale;
    float x;
    float y;

    bool operator==(const EnlargerKey &other) const {
        return sourceRevision == other.sourceRevision && scale == other.scale && x == other.x && y == other.y;
    }
};

struct EnlargerCache {
    static constexpr uint64_t maxBytes = 512ull * 1024 * 1024;
    static constexpr uint64_t maxInactiveBytes = 192ull * 1024 * 1024;
    uint64_t sequence = 0; // Shared by original and transformed sources in this worker.
    uint64_t revision = 0;
    std::optional<EnlargerKey> key;
    std::vector<float> pixels;

    void clear() {
        key.reset();
        revision = 0;
        std::vector<float>().swap(pixels);
    }
};

struct FrameLimit {
    int maxSide;
    int maxPixels;
    const char *pixelError;
};

constexpr FrameLimit kPreviewLimit{2048, 1048576, "The in-app spatial preview accepts up to 1,048,576 pixels."};
constexpr FrameLimit kExportLimit{16384, 100000000, "The Spektrafilm export renderer accepts up to 100,000,000 pixels."};
constexpr FrameLimit kNativePreviewLimit{16384, 100000000, "The native Viewer accepts up to 100,000,000 pixels."};

constexpr int kNativeFullFramePixels = 32000000;

// Protocol version is independent of package version. Old one-shot/stream clients
// still work; the managed host negotiates before submitting any frame bytes.
#ifndef SPEKTRA_PARAMETER_CONTRACT
#error "The renderer build must identify its parameter contract."
#endif

std::streamsize byteCount(std::size_t components) {
    static_assert(sizeof(float) == 4, "The frame protocol uses 32-bit floats.");
    return static_cast<std::streamsize>(components * sizeof(float));
}

FrameSize readFrameSize(const std::string &widthText, const std::string &heightText, const FrameLimit &limit) {
    const auto width = integer(widthText, 1, limit.maxSide);
    const auto height = integer(heightText, 1, limit.maxSide);
    if (width * height > limit.maxPixels)
        throw std::invalid_argument(limit.pixelError);
    return {width, height};
}

void checkFrameLength(std::streamsize expected) {
    if (std::cin.gcount() != expected)
        throw std::invalid_argument("The input frame has the wrong byte length.");
}

InputFrame readFrame(FrameSize size) {
    InputFrame frame{size, std::vector<float>(size.componentCount())};
    const auto expected = byteCount(frame.pixels.size());
    std::cin.read(reinterpret_cast<char *>(frame.pixels.data()), expected);
    checkFrameLength(expected);
    return frame;
}

void skipFrame(FrameSize size) {
    const auto expected = byteCount(size.componentCount());
    std::cin.ignore(expected);
    checkFrameLength(expected);
}

bool hasFinitePixels(const std::vector<float> &pixels, const RequestCancellation *stop = nullptr) {
    constexpr size_t block = 64 * 1024;
    for (size_t index = 0; index < pixels.size(); index += block) {
        if (stop) stop->check();
        const auto first = pixels.begin() + index;
        const auto end = first + std::min(block, pixels.size() - index);
        if (!std::all_of(first, end, [](float component) { return std::isfinite(component); }))
            return false;
    }
    return true;
}

std::unique_ptr<Renderer> startRenderer() {
    auto renderer = createNativeRenderer();
    if (!renderer || !renderer->isAvailable())
        throw std::runtime_error(renderer ? renderer->lastError() : "Vulkan renderer unavailable.");
    return renderer;
}

struct FrameMemoryPlan {
    uint64_t fullFrameBytes = 0;
    uint64_t budgetBytes = 0;
    uint64_t reclaimableDeviceHeapBytes = 0;
    bool dynamicBudget = false;
    bool selectedTiling = false;
};

struct FrameMetrics {
    FrameMemoryPlan memory;
    double enlargerMs = 0;
    bool enlargerCacheHit = false;
    uint64_t enlargerCachedBytes = 0;
};

uint64_t estimateFullFrameGpuBytes(FrameSize size, const RenderParams &params) {
    const auto pixels = static_cast<uint64_t>(size.width) * static_cast<uint64_t>(size.height);
    uint64_t perPixel = 112; // Packed color alone used about 96 B/px in the benchmark.
    if (params.halationEnabled) perPixel += 80;
    if (params.cameraDiffusionEnabled) perPixel += 64;
    if (params.printDiffusionEnabled) perPixel += 64;
    if (params.dirCouplersAmount > 1.0f) perPixel += 24;
    if (params.scannerEnabled) perPixel += 32;
    if (params.grainEnabled) perPixel += params.grainModel == GrainModel::GrainSynthesis ? 160 : 32;
    return 64ull * 1024ull * 1024ull + pixels * perPixel;
}

FrameMemoryPlan planGpuMemory(Renderer &renderer, FrameSize size, RenderParams &params) {
    constexpr uint64_t mebibyte = 1024ull * 1024ull;
    const auto *vulkan = dynamic_cast<const VulkanRenderer *>(&renderer);
    if (!vulkan) return {};
    const auto budget = vulkan->availableDeviceLocalMemory();
    const auto pixels = static_cast<uint64_t>(size.width) * static_cast<uint64_t>(size.height);
    const auto estimate = estimateFullFrameGpuBytes(size, params);
    const auto headroom = budget.bytes - budget.bytes / 8;
    FrameMemoryPlan plan{estimate, headroom, budget.reclaimableBytes, budget.dynamic, false};
    if (headroom == 0 && !budget.dynamic) return plan;
    if (params.gpuRenderTiling == GpuRenderTilingMode::Tiled || estimate > headroom) {
        if (params.grainEnabled && params.grainModel == GrainModel::GrainSynthesis)
            throw std::runtime_error("Grain Synthesis needs more GPU memory for this full frame. Choose a smaller resolution or a different grain model.");
        // Tiling still needs two full-size source buffers plus tile scratch.
        // This is a minimum; overlap requirements vary with optical parameters.
        if (64 * mebibyte + pixels * 32 > headroom)
            throw std::runtime_error("This frame needs more GPU memory for the source buffers. Free GPU memory or choose a smaller resolution.");
        if (renderer.lastDiagnostics().transientCachedBytes != 0 && !renderer.lastDiagnostics().tiledRendering)
            renderer.releaseTransientResources();
        plan.selectedTiling = params.gpuRenderTiling != GpuRenderTilingMode::Tiled;
        params.gpuRenderTiling = GpuRenderTilingMode::Tiled;
    }
    return plan;
}

FrameMetrics renderFrame(Renderer &renderer, const InputFrame &input, RenderParams params, std::vector<float> &output,
                         EnlargerCache *cache = nullptr, std::vector<uint32_t> *display = nullptr,
                         const RequestCancellation *stop = nullptr, std::optional<uint64_t> progress = std::nullopt) {
    if (stop) stop->check();
    FrameMetrics metrics{planGpuMemory(renderer, input.size, params)};
    const auto width = input.size.width;
    const auto height = input.size.height;
    const auto *pixels = input.pixels.data();
    auto revision = input.revision;
    const uint64_t bytes = input.pixels.size() * sizeof(float);
    std::vector<float> enlarged;
    if (std::abs(params.enlargerScale - 1.0f) > 1.0e-6f) {
        const EnlargerKey key{input.revision, params.enlargerScale, params.enlargerOffsetXPercent, params.enlargerOffsetYPercent};
        if (cache && cache->key && *cache->key == key) {
            metrics.enlargerCacheHit = true;
        } else {
            if (cache) cache->clear();
            const auto start = std::chrono::steady_clock::now();
            enlarged = resampleEnlarger(input.pixels, width, height, params, stop);
            metrics.enlargerMs = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count();
            if (cache && bytes <= EnlargerCache::maxBytes) {
                cache->pixels = std::move(enlarged);
                cache->key = key;
                cache->revision = ++cache->sequence;
            }
        }
        if (cache && cache->key) {
            pixels = cache->pixels.data();
            revision = cache->revision;
            metrics.enlargerCachedBytes = bytes;
        } else {
            pixels = enlarged.data();
            revision = 0;
        }
    } else if (cache && cache->key) {
        if (cache->key->sourceRevision != input.revision || bytes > EnlargerCache::maxInactiveBytes)
            cache->clear();
        else
            metrics.enlargerCachedBytes = bytes;
    }

    const int rowBytes = width * 4 * sizeof(float);
    const ImageView source{pixels, 0, 0, width, height, rowBytes, 4, sizeof(float), revision};
    MutableImageView destination{nullptr, 0, 0, width, height, rowBytes, 4, sizeof(float)};
    if (display) {
        if (!output.empty()) std::vector<float>().swap(output);
        display->resize(input.pixels.size() / 4);
        destination.data = display->data();
        destination.rowBytes = width * 4;
        destination.bytesPerComponent = 1;
    } else {
        output.resize(input.pixels.size());
        destination.data = output.data();
    }
    const RenderWindow frame{0, 0, width, height};
    if (stop) stop->check();
    auto *vulkan = dynamic_cast<VulkanRenderer *>(&renderer);
    if (vulkan)
        vulkan->setCancellationProbe(stop ? &RequestCancellation::poll : nullptr, stop);
    const uint64_t alternateRevision = cache && cache->key && bytes <= EnlargerCache::maxInactiveBytes
        ? (revision == input.revision ? cache->revision : input.revision) : 0u;
    writeProgress(progress, WorkerStage::Vulkan);
    const bool rendered = vulkan
        ? vulkan->render(source, destination, frame, params, 0.0, alternateRevision)
        : renderer.render(source, destination, frame, params, 0.0);
    if (stop) stop->check();
    if (!rendered) {
        const auto error = renderer.lastError();
        if (error.find("VK_ERROR_DEVICE_LOST") != std::string::npos) throw RenderDeviceLost(error);
        throw std::runtime_error(error);
    }
    writeProgress(progress, WorkerStage::Verifying);
    if (!hasFinitePixels(output, stop)) throw std::runtime_error("Renderer produced a non-finite pixel.");
    return metrics;
}

void process(int argc, char **argv) {
    if (argc < 3 || argc % 2 == 0)
        throw std::invalid_argument("Usage: SpektraStillRenderer WIDTH HEIGHT [--setting VALUE ...], --stream, --export-stream, or --native-preview-stream");
    const auto params = readParameters(std::vector<std::string>(argv + 3, argv + argc));
    enableBinaryPipes();
    const auto input = readFrame(readFrameSize(argv[1], argv[2], kPreviewLimit));
    if (!hasFinitePixels(input.pixels)) throw std::invalid_argument("Invalid input pixel.");
    const auto renderer = startRenderer();
    std::vector<float> output;
    renderFrame(*renderer, input, params, output);
    std::cout.write(reinterpret_cast<const char *>(output.data()), byteCount(output.size()));
    if (!std::cout)
        throw std::runtime_error("Could not send the rendered frame to Capture One.");
}

bool readCommand(std::string &line) {
    constexpr std::size_t limit = 16384;
    line.clear();
    while (true) {
        const auto ch = std::cin.rdbuf()->sbumpc();
        if (ch == std::char_traits<char>::eof()) {
            if (line.empty()) return false;
            throw std::invalid_argument("The renderer request ended before its line break.");
        }
        if (ch == '\n') return true;
        if (line.size() == limit)
            throw std::invalid_argument("The renderer request exceeds 16 KB.");
        if ((ch < ' ' || ch > '~') && ch != '\t' && ch != '\r')
            throw std::invalid_argument("The renderer command must contain ASCII text.");
        line.push_back(static_cast<char>(ch));
    }
}

void writeReplyHeader(uint32_t status, uint32_t length, std::optional<uint64_t> request = std::nullopt) {
    std::array<char, 16> header{};
    for (std::size_t byte = 0; byte < 4; ++byte) {
        header[byte] = static_cast<char>((status >> (byte * 8)) & 0xff);
        header[byte + 4] = static_cast<char>((length >> (byte * 8)) & 0xff);
    }
    if (request)
        for (std::size_t byte = 0; byte < 8; ++byte)
            header[byte + 8] = static_cast<char>((*request >> (byte * 8)) & 0xff);
    std::cout.write(header.data(), request ? header.size() : 8);
}

void flushReply() {
    std::cout.flush();
    if (!std::cout)
        throw std::runtime_error("Could not send the rendered frame to Capture One.");
}

void writeReply(uint32_t status, const char *data, uint32_t length, std::optional<uint64_t> request = std::nullopt) {
    writeReplyHeader(status, length, request);
    if (length != 0) std::cout.write(data, length);
    flushReply();
}

void writeProgress(std::optional<uint64_t> request, WorkerStage stage) {
    if (!request) return;
    const char value = static_cast<char>(stage);
    writeReply(4, &value, sizeof(value), request);
}

__m128i displayPixel(const float *rendered, const float *source) {
    const auto zero = _mm_setzero_ps();
    const auto one = _mm_set1_ps(1.0f);
    const auto rgb = _mm_min_ps(one, _mm_max_ps(zero, _mm_loadu_ps(rendered)));
    const auto scale = _mm_set1_pd(255.0);
    // Match the managed path's double RGB multiplication and round-to-even conversion.
    const auto rg = _mm_cvtpd_epi32(_mm_mul_pd(_mm_cvtps_pd(rgb), scale));
    const auto ba = _mm_cvtpd_epi32(_mm_mul_pd(_mm_cvtps_pd(_mm_movehl_ps(rgb, rgb)), scale));
    const auto bgra = _mm_shuffle_epi32(_mm_unpacklo_epi64(rg, ba), _MM_SHUFFLE(3, 0, 1, 2));
    // Display alpha comes from the original source and is multiplied in float precision.
    const auto sourcePixel = _mm_loadu_ps(source);
    const auto a = _mm_shuffle_ps(sourcePixel, sourcePixel, _MM_SHUFFLE(3, 3, 3, 3));
    const auto alpha = _mm_cvtps_epi32(_mm_mul_ps(_mm_min_ps(one, _mm_max_ps(zero, a)), _mm_set1_ps(255.0f)));
    const auto alphaMask = _mm_set_epi32(-1, 0, 0, 0);
    return _mm_or_si128(_mm_andnot_si128(alphaMask, bgra), _mm_and_si128(alphaMask, alpha));
}

void writePackedPreview(const InputFrame &input, const std::vector<float> &rendered, std::optional<uint64_t> request) {
    alignas(16) std::array<char, 64 * 1024> buffer;
    writeReplyHeader(0, static_cast<uint32_t>(rendered.size()), request);
    for (std::size_t start = 0; start < rendered.size(); start += buffer.size()) {
        const auto count = std::min(buffer.size(), rendered.size() - start);
        const auto vectorizedCount = count - count % 16;
        for (std::size_t offset = 0; offset < vectorizedCount; offset += 16) {
            const auto index = start + offset;
            const auto *rgb = rendered.data() + index;
            const auto *alpha = input.pixels.data() + index;
            const auto first = _mm_packs_epi32(displayPixel(rgb, alpha), displayPixel(rgb + 4, alpha + 4));
            const auto second = _mm_packs_epi32(displayPixel(rgb + 8, alpha + 8), displayPixel(rgb + 12, alpha + 12));
            _mm_store_si128(reinterpret_cast<__m128i *>(buffer.data() + offset), _mm_packus_epi16(first, second));
        }
        for (std::size_t offset = vectorizedCount; offset < count; offset += 4) {
            const auto index = start + offset;
            const auto words = _mm_packs_epi32(displayPixel(rendered.data() + index, input.pixels.data() + index), _mm_setzero_si128());
            const auto pixel = _mm_cvtsi128_si32(_mm_packus_epi16(words, _mm_setzero_si128()));
            std::memcpy(buffer.data() + offset, &pixel, sizeof(pixel));
        }
        if (!std::cout.write(buffer.data(), count))
            throw std::runtime_error("Could not send the rendered frame to Capture One.");
    }
    flushReply();
}

enum class ReplyError : uint32_t { Rejected = 1, Desynchronized = 2, DeviceLost = 5 };

void writeError(std::string_view message, std::optional<uint64_t> request = std::nullopt, ReplyError kind = ReplyError::Rejected) {
    if (message.size() > 4096) message = message.substr(0, 4096);
    const auto status = request ? static_cast<uint32_t>(kind) : static_cast<uint32_t>(ReplyError::Rejected);
    writeReply(status, message.data(), static_cast<uint32_t>(message.size()), request);
}

void writeCapabilities(const FrameLimit &limit, int protocol, bool cancelAvailable, bool progressAvailable) {
    std::ostringstream json;
    json << "{\"protocol\":" << protocol
         << ",\"floatChannels\":4,\"floatBits\":32,\"littleEndian\":true,\"parameterContract\":\""
         << SPEKTRA_PARAMETER_CONTRACT
         << "\",\"maxSide\":" << limit.maxSide << ",\"maxPixels\":" << limit.maxPixels
         << ",\"packedDisplay\":" << (&limit == &kNativePreviewLimit ? "true" : "false")
         << ",\"metricsSchema\":1";
    if (protocol == kProtocolVersion)
        json << ",\"requestHeaderBytes\":16,\"floatLayout\":\"rgba32f\",\"packedLayout\":\"bgra8\",\"requestCancellation\":"
             << (cancelAvailable ? "true" : "false")
             << ",\"frameProgress\":" << (progressAvailable ? "true" : "false") << ",\"gpuPlanSchema\":1";
    json << '}';
    const auto text = json.str();
    writeReply(0, text.data(), static_cast<uint32_t>(text.size()));
}

void writeMetrics(const Renderer *renderer, uint64_t renderSequence, const InputFrame &input, const FrameMetrics &metrics,
                  std::optional<uint64_t> request) {
    const RendererDiagnostics empty{};
    const auto &d = renderer ? renderer->lastDiagnostics() : empty;
    const auto &memory = metrics.memory;
    std::ostringstream json;
    json << "{\"schema\":1,\"renderSequence\":" << renderSequence
         << ",\"sourceRevision\":" << input.revision
         << ",\"width\":" << input.size.width << ",\"height\":" << input.size.height
         << ",\"cpuSetupMs\":" << d.cpuSetupMs
         << ",\"sourceCopyMs\":" << d.sourceCopyMs
         << ",\"meteringMs\":" << d.meteringMs
         << ",\"meteringCacheHit\":" << (d.meteringCacheHit ? "true" : "false")
         << ",\"negativeDensityCacheHit\":" << (d.negativeDensityCacheHit ? "true" : "false")
         << ",\"commandEncodingMs\":" << d.commandEncodingMs
         << ",\"commandBufferMs\":" << d.commandBufferMs
         << ",\"gpuCommandBufferMs\":" << d.gpuCommandBufferMs
         << ",\"gpuTimingAvailable\":" << (d.passGpuTimingAvailable ? "true" : "false")
         << ",\"outputCopyMs\":" << d.outputCopyMs
         << ",\"readbackBytes\":" << d.readbackBytes
         << ",\"uploadBytes\":" << d.uploadBytes
         << ",\"enlargerMs\":" << metrics.enlargerMs
         << ",\"enlargerCacheHit\":" << (metrics.enlargerCacheHit ? "true" : "false")
         << ",\"enlargerCachedBytes\":" << metrics.enlargerCachedBytes
         << ",\"staticAllocationBytes\":" << d.staticAllocationBytes
         << ",\"scratchAllocationBytes\":" << d.scratchAllocationBytes
         << ",\"transientCachedBytes\":" << d.transientCachedBytes
         << ",\"transientBudgetBytes\":" << d.transientBudgetBytes
         << ",\"alternateSourceCachedBytes\":" << d.alternateSourceCachedBytes
         << ",\"passCount\":" << d.passCount
         << ",\"tiled\":" << (d.tiledRendering ? "true" : "false")
         << ",\"tileCount\":" << d.tileCount
         << ",\"tileWidth\":" << d.tileWidth
         << ",\"tileHeight\":" << d.tileHeight
         << ",\"tileOverlap\":" << d.tileOverlap
         << ",\"estimatedFullFrameGpuBytes\":" << memory.fullFrameBytes
         << ",\"gpuMemoryBudgetBytes\":" << memory.budgetBytes
         << ",\"reclaimableDeviceHeapBytes\":" << memory.reclaimableDeviceHeapBytes
         << ",\"dynamicGpuBudget\":" << (memory.dynamicBudget ? "true" : "false")
         << ",\"budgetSelectedTiling\":" << (memory.selectedTiling ? "true" : "false") << '}';
    const auto text = json.str();
    writeReply(0, text.data(), static_cast<uint32_t>(text.size()), request);
}

void configureLargeFrame(RenderParams &params, FrameSize size, bool nativePreview) {
    if (size.width * size.height <= kNativeFullFramePixels) return;
    if (params.grainEnabled && params.grainModel == GrainModel::GrainSynthesis)
        throw std::invalid_argument(nativePreview
            ? "Native Viewer grain synthesis supports up to 32,000,000 pixels. Choose a smaller Viewer preview size or switch grain models."
            : "Export grain synthesis supports up to 32,000,000 pixels. Choose a smaller Export Recipe or switch grain models.");
    // Use the OFX renderer's overlap and full-frame coordinates to bound the
    // optical scratch buffers while keeping the original sampling resolution.
    params.gpuRenderTiling = GpuRenderTilingMode::Tiled;
}

void writeGpuPlan(const Renderer &renderer, FrameSize size, const RenderParams &params, uint64_t request) {
    const auto *vulkan = dynamic_cast<const VulkanRenderer *>(&renderer);
    const auto id = vulkan ? vulkan->deviceIdentifier() : std::string{};
    const auto memory = vulkan ? vulkan->availableDeviceLocalMemory() : VulkanMemoryBudget{};
    const auto estimate = estimateFullFrameGpuBytes(size, params);
    const auto budget = std::min(memory.heapBudgetBytes, memory.heapCapacityBytes);
    const auto resident = memory.heapUsageBytes - memory.reclaimableBytes;
    const auto shared = memory.dynamic ? budget - std::min(budget, resident) : memory.bytes;
    std::ostringstream json;
    json << "{\"schema\":1,\"adapter\":";
    if (id.empty() || memory.heapIndex == UINT32_MAX) json << "null";
    else json << '"' << id << '"';
    json << ",\"heap\":" << memory.heapIndex
         << ",\"heapCapacityBytes\":" << memory.heapCapacityBytes
         << ",\"driverBudgetBytes\":" << memory.heapBudgetBytes
         << ",\"driverUsageBytes\":" << memory.heapUsageBytes
         << ",\"retainedBytes\":" << memory.retainedBytes
         << ",\"reclaimableBytes\":" << memory.reclaimableBytes
         << ",\"planningBytes\":" << (memory.bytes - memory.bytes / 8)
         << ",\"sharedPlanningBytes\":" << (shared - shared / 8)
         << ",\"estimatedFullFrameBytes\":" << estimate << '}';
    const auto text = json.str();
    writeReply(0, text.data(), static_cast<uint32_t>(text.size()), request);
}

void processStream(const FrameLimit &limit) {
    enableBinaryPipes();
    const RequestCancellation cancellation;
    char wantProgress[2]{};
    size_t progressLength = 0;
    const bool progressEnabled = cancellation.available() &&
        getenv_s(&progressLength, wantProgress, sizeof(wantProgress), "SPEKTRAFILM_REQUEST_PROGRESS") == 0 &&
        progressLength == 2 && wantProgress[0] == '1';
    std::unique_ptr<Renderer> renderer;
    InputFrame input{};
    std::vector<float> output;
    std::vector<uint32_t> packedOutput;
    std::optional<std::vector<std::string>> renderedArguments;
    const bool nativePreview = &limit == &kNativePreviewLimit;
    size_t copyExperimentLength = 0;
    getenv_s(&copyExperimentLength, nullptr, 0, "SPEKTRAFILM_VULKAN_COPY_PASS");
    uint64_t renderSequence = 0;
    uint64_t lastRequest = 0;
    bool versioned = false;
    std::optional<uint64_t> request;
    EnlargerCache enlarger;
    FrameMetrics metrics{};
    std::string line;
    try {
        while (true) {
            request = versioned ? std::optional<uint64_t>(0) : std::nullopt;
            if (!readCommand(line)) break;
            std::istringstream command(line);
            std::string verb;
            command >> verb;
            if (versioned) {
                std::string identifier;
                if (verb != "REQUEST" || !(command >> identifier >> verb))
                    throw std::invalid_argument("Expected a numbered renderer request.");
                uint64_t number = 0;
                const auto end = identifier.data() + identifier.size();
                const auto parsed = std::from_chars(identifier.data(), end, number);
                if (parsed.ec != std::errc{} || parsed.ptr != end || number <= lastRequest)
                    throw std::invalid_argument("Invalid or repeated renderer request number.");
                request = lastRequest = number;
            } else if (verb == "HELLO") {
                std::string version, extra;
                if (!(command >> version) || (version != "1" && version != "2") || (command >> extra)) {
                    writeError("The renderer supports HELLO 1 and HELLO 2. Install a matching panel and renderer.");
                    continue;
                }
                versioned = version == "2";
                writeCapabilities(limit, versioned ? kProtocolVersion : 1, cancellation.available(), progressEnabled);
                continue;
            }
            if (verb == "METRICS") {
                std::string extra;
                if (command >> extra) writeError("METRICS does not take arguments.", request);
                else writeMetrics(renderer.get(), renderSequence, input, metrics, request);
                continue;
            }
            if (versioned && verb == "PLAN") {
                try {
                    std::string width, height;
                    command >> width >> height;
                    const auto size = readFrameSize(width, height, limit);
                    std::vector<std::string> args;
                    for (std::string option; command >> option;) args.push_back(std::move(option));
                    auto params = readParameters(args);
                    if (nativePreview || &limit == &kExportLimit)
                        configureLargeFrame(params, size, nativePreview);
                    if (!renderer) renderer = startRenderer();
                    writeGpuPlan(*renderer, size, params, *request);
                } catch (const std::exception &error) {
                    writeError(error.what(), request);
                }
                continue;
            }
            if (verb == "QUIT") {
                std::string extra;
                if (command >> extra) {
                    writeError("QUIT does not take arguments.", request);
                    continue;
                }
                writeReply(0, nullptr, 0, request);
                return;
            }

            const bool packedPreview = nativePreview && (verb == "FRAME8" || verb == "REUSE8");
            const bool replacesInput = verb == "FRAME" || (packedPreview && verb == "FRAME8");
            FrameSize size{};
            if (!replacesInput && verb != "REUSE" && !packedPreview)
                throw std::invalid_argument("Expected a FRAME, REUSE, or QUIT renderer command.");
            if (replacesInput || versioned) {
                std::string width, height;
                command >> width >> height;
                // An invalid frame length makes locating the next header impossible.
                size = readFrameSize(width, height, limit);
            }

            std::vector<std::string> args;
            for (std::string option; command >> option;) args.push_back(std::move(option));
            RenderParams params;
            try {
                if (!replacesInput && input.pixels.empty())
                    throw std::invalid_argument("Send a FRAME before requesting REUSE.");
                if (!replacesInput && versioned && (size.width != input.size.width || size.height != input.size.height))
                    throw std::invalid_argument("REUSE dimensions do not match the accepted source.");
                params = readParameters(args);
                if (packedPreview && params.outputColorSpace != ColorSpace::Srgb)
                    throw std::invalid_argument("Packed Viewer pixels require sRGB output.");
                if (nativePreview || &limit == &kExportLimit)
                    configureLargeFrame(params, replacesInput ? size : input.size, nativePreview);
            } catch (const std::exception &error) {
                if (replacesInput) skipFrame(size);
                writeError(error.what(), request);
                continue;
            }

            const RequestCancellation *stop = versioned && cancellation.available() ? &cancellation : nullptr;
            const auto progress = progressEnabled ? request : std::nullopt;
            InputFrame replacement{};
            if (replacesInput) replacement = readFrame(size); // Always consume the complete protocol payload.
            try {
                if (stop) stop->check();
                writeProgress(progress, WorkerStage::SourceAccepted);
                if (replacesInput) {
                    if (!hasFinitePixels(replacement.pixels, stop)) {
                        writeError("Invalid input pixel.", request);
                        continue;
                    }
                    if (nativePreview) replacement.revision = ++enlarger.sequence;
                }
                if (replacesInput || !renderedArguments || args != *renderedArguments || (!packedPreview && !packedOutput.empty())) {
                    renderedArguments.reset();
                    writeProgress(progress, WorkerStage::Preparing);
                    if (!renderer) renderer = startRenderer();
                    // Transformed alpha uses the original source through the CPU packer.
                    const bool packOnGpu = packedPreview && copyExperimentLength == 0 &&
                        std::abs(params.enlargerScale - 1.0f) <= 1.0e-6f;
                    if (!packOnGpu && !packedOutput.empty()) std::vector<uint32_t>().swap(packedOutput);
                    metrics = renderFrame(*renderer, replacesInput ? replacement : input, params, output,
                                          nativePreview ? &enlarger : nullptr, packOnGpu ? &packedOutput : nullptr, stop, progress);
                    if (stop) stop->check();
                    ++renderSequence;
                    if (replacesInput) input = std::move(replacement);
                    if (nativePreview) renderedArguments = std::move(args);
                }
                if (stop) stop->check();
            } catch (const RenderCanceled &) {
                // A cancelled source or partially rendered frame is never eligible for reuse.
                // A subsequent FRAME can still keep this worker's loaded Vulkan resources.
                renderedArguments.reset();
                enlarger.clear();
                metrics = {};
                writeReply(3, nullptr, 0, request);
                continue;
            } catch (const std::exception &error) {
                renderer.reset();
                enlarger.clear();
                metrics = {};
                const auto kind = dynamic_cast<const RenderDeviceLost *>(&error) ? ReplyError::DeviceLost : ReplyError::Rejected;
                writeError(error.what(), request, kind);
                continue;
            }
            writeProgress(progress, WorkerStage::Sending);
            if (packedPreview && !packedOutput.empty()) {
                writeReply(0, reinterpret_cast<const char *>(packedOutput.data()),
                           static_cast<uint32_t>(packedOutput.size() * sizeof(uint32_t)), request);
            } else if (packedPreview) {
                writePackedPreview(input, output, request);
            } else {
                writeReply(0, reinterpret_cast<const char *>(output.data()), static_cast<uint32_t>(byteCount(output.size())), request);
            }
        }
    } catch (const std::exception &error) {
        if (std::cout) writeError(error.what(), request, ReplyError::Desynchronized);
        throw;
    }
}

} // namespace

int main(int argc, char **argv) {
    try {
        const auto mode = argc == 2 ? std::string_view(argv[1]) : std::string_view{};
        if (mode == "--stream") processStream(kPreviewLimit);
        else if (mode == "--export-stream") processStream(kExportLimit);
        else if (mode == "--native-preview-stream") processStream(kNativePreviewLimit);
        else if (mode == "--describe") describeParameters();
        else process(argc, argv);
        return 0;
    } catch (const std::exception &error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
