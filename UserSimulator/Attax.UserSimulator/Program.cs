using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Attax.Agents;
using Attax.Core;
using Attax.Core.Utils;
using Attax.Play;
using CommandLine;

namespace Attax.UserSimulator {
    public static class Program {
        public static async Task<int> Main(string[] args) {
            BotOptions? options = null;

            var parser = new Parser(cfg => {
                cfg.CaseInsensitiveEnumValues = true;
                cfg.HelpWriter = Console.Error;
            });

            parser.ParseArguments<BotOptions>(args)
                .WithParsed(o => options = o)
                .WithNotParsed(_ => Environment.Exit(2));

            if (options == null) return 2;

            try {
                options.Validate();
            } catch (Exception ex) {
                Console.Error.WriteLine($"Validation error: {ex.Message}");
                return 2;
            }

            var log = BotLogging.Create(options);
            log.Information("Starting Attax.UserSimulator for bot {BotName} (Mode={Mode})", options.Name, options.Mode);

            try {
                string specText = options.Spec;
                if (options.Cores.HasValue && !specText.Contains("cores=", StringComparison.OrdinalIgnoreCase)) {
                    if (specText.Contains(':')) {
                        specText += $",cores={options.Cores.Value}";
                    } else if (!specText.Equals("random", StringComparison.OrdinalIgnoreCase)) {
                        specText += $":cores={options.Cores.Value}";
                    }
                }

                var spec = PlayerSpec.Parse(specText);
                if (spec.Depth > 3) {
                    throw new ArgumentException($"AI depth cannot exceed 3, got depth={spec.Depth}.");
                }

                using var player = PlayerFactory.Create(spec, null!, options.Seed, defaultTrain: false);

                AiAbilityPolicy? abilityPolicy = null;
                if (options.Abilities == "policy") {
                    string cfgPath = options.AbilityConfig;
                    if (!File.Exists(cfgPath)) {
                        cfgPath = Path.Combine(AppContext.BaseDirectory, options.AbilityConfig);
                    }
                    if (File.Exists(cfgPath)) {
                        string json = File.ReadAllText(cfgPath);
                        var cfg = Newtonsoft.Json.JsonConvert.DeserializeObject<AiAbilityConfig>(json);
                        abilityPolicy = new AiAbilityPolicy(cfg!, options.Seed != 0 ? options.Seed : 1234, enabled: true);
                    } else {
                        log.Warning("Ability config file {Path} not found. Running with abilities disabled.", options.AbilityConfig);
                    }
                }

                var planner = new AgentTurnPlanner(player, abilityPolicy);

                await using var connection = new ServerConnection(options, log);
                await connection.StartAsync();

                var gamePlayer = new GamePlayer(options, connection, planner, log);

                if (options.Mode == "lobby") {
                    var lobbyBot = new LobbyBot(options, connection, gamePlayer, log);
                    await lobbyBot.RunAsync();
                } else {
                    var privateBot = new PrivateRoomBot(options, connection, gamePlayer, log, new StandardConsoleIO());
                    await privateBot.RunAsync();
                }

                log.Information("Bot {BotName} completed run cleanly", options.Name);
                return 0;
            } catch (ServerErrorException ex) {
                log.Error(ex, "Server error terminated bot {BotName}", options.Name);
                return 3;
            } catch (Exception ex) {
                log.Fatal(ex, "Unhandled exception in bot {BotName}", options.Name);
                return 5;
            }
        }
    }
}
