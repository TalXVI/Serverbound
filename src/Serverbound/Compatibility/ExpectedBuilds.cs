namespace Serverbound.Compatibility
{
    internal static class ExpectedBuilds
    {
        // 0.21.0/0.21.1 use direct skill reads; 0.21.3 adds guarded cap helpers.
        internal static readonly string[] ImpactfulSkills =
        {
            "ADE4FD2943A886B9B90C0241CB77EC65A97C1840EF39A906B58BAF3925086706",
            "D6E0844F3DADE42B23C33A678EB6E3D6DAA288DCC4187F497B8BB2A1F3CF47BA",
            "52DB5951ADDA57426706A58C9F7C530DFD7519A3E987C73516992111588EF656"
        };
        internal static readonly string[] ValheimCommunityPatch =
        {
            "E48804F2280878B1BB30393C57C354EFDAA68F106A0B2C77F150C113A25EAA68",
            "DF171F6A25C8AEB325F98EBDDD29088A0FF6EE7B5F9EDC88E9E6F5F63E3A9014"
        };
        // 1.0.16 and 1.0.17 clients; 1.0.17 changes no method these patches hook.
        internal static readonly string[] Valheim =
        {
            "96CFC004F7F4A6F30D070BEF39EAFD79C466A137121C4665A2F19FB9C15C6127",
            "25A0A107DCE4D834C44C2B72D0EAFD5CB7793933BDA81816ACCFA1EA9543DACE"
        };
        internal static readonly string[] Server =
        {
            "7CAB9B49D31EC064591CA80402DD35C566E03B7297CFB7BF4696C38DA4E24D8B",
            "50035055F9B158A025CACD25E038B603943F7C2A465DA3021707B5F1E44E39FD",
            "0DFC7E81436F822121148EFED859BA1E661D58B9484BD45E3B6F33B6DD86BFAD",
            "E7220DC5D9CF9D38270E751352D94918308E59CCA86E56C4BDA0210016C847FA"
        };

        // OdinShip 0.8.7 candidate audited for owner-routed customization and turret-mode inputs.
        internal static readonly string[] OdinShip =
        {
            "5D5817BC169677DB49CAE55986B3F6A178AB3BF0B1D681A2ABA4ECC5DAC067D0"
        };
    }
}
