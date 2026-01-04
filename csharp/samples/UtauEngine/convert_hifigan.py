"""
HiFi-GAN ONNX 変換スクリプト（簡易版）
既にPyTorchモデル（.pth）がある場合に使用
"""

import torch
import argparse
import sys

def convert_to_onnx(checkpoint_path, output_path='hifigan.onnx'):
    try:
        # HiFi-GAN リポジトリが必要
        from models import Generator
        from env import AttrDict
    except ImportError:
        print("ERROR: HiFi-GAN repository not found.")
        print("Please clone: git clone https://github.com/jik876/hifi-gan")
        print("Then run this script from inside the hifi-gan directory.")
        sys.exit(1)
    
    # Universal V1 設定
    h = AttrDict({
        'resblock': '1',
        'upsample_rates': [8, 8, 2, 2],
        'upsample_kernel_sizes': [16, 16, 4, 4],
        'upsample_initial_channel': 512,
        'resblock_kernel_sizes': [3, 7, 11],
        'resblock_dilation_sizes': [[1, 3, 5], [1, 3, 5], [1, 3, 5]],
        'num_mels': 80
    })
    
    print(f"Loading model from {checkpoint_path}...")
    generator = Generator(h).eval()
    
    # チェックポイント読み込み
    state_dict = torch.load(checkpoint_path, map_location='cpu')
    if 'generator' in state_dict:
        generator.load_state_dict(state_dict['generator'])
    else:
        generator.load_state_dict(state_dict)
    
    print("Converting to ONNX...")
    dummy_input = torch.randn(1, 80, 100)  # [batch, mels, time]
    
    # ONNX外部データを使わないように設定
    import onnx
    torch.onnx.export(
        generator,
        dummy_input,
        output_path,
        input_names=['mel'],
        output_names=['audio'],
        dynamic_axes={
            'mel': {2: 'time'},
            'audio': {1: 'samples'}
        },
        opset_version=18,
        do_constant_folding=True,
        verbose=False
    )
    
    # モデルを読み込んで、external dataなしで再保存
    print("Converting external data to single file...")
    onnx_model = onnx.load(output_path)
    onnx.save(onnx_model, output_path, save_as_external_data=False)
    
    print(f"✓ ONNX model saved: {output_path}")
    print(f"\nModel info:")
    print(f"  Input:  mel [1, 80, T] (Mel-spectrogram)")
    print(f"  Output: audio [1, samples]")
    print(f"\nCopy this file to: publish/models/hifigan.onnx")

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description='Convert HiFi-GAN to ONNX')
    parser.add_argument('checkpoint', help='Path to .pth checkpoint file')
    parser.add_argument('--output', default='hifigan.onnx', help='Output ONNX path')
    
    args = parser.parse_args()
    convert_to_onnx(args.checkpoint, args.output)
